using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Analytics.Core;

/// <summary>
/// Produces an anonymous visitor identifier: HMAC-SHA256(daily salt, site | ip | user agent), truncated.
/// The salt is random and changes every UTC day; the previous day's salt is deleted with the first event of the new
/// day, so identifiers cannot be linked across days and cannot be reversed to an IP address once the salt is gone.
/// The salt is stored only sealed by <see cref="SaltSealer"/>, whose key lives in the ingest configuration: a
/// database dump or backup alone is not enough to brute-force today's ids back to IP addresses. Sharing the stored
/// salt lets several ingest instances (and restarts) agree on visitor ids. Neither the IP nor the user agent is stored.
/// </summary>
public sealed class VisitorHasher(ISaltStore salts, SaltSealer sealer, ILogger<VisitorHasher>? log = null)
{
    private readonly ConcurrentDictionary<DateOnly, byte[]> _cache = new();

    /// <summary>
    /// Without a salt key: the salt exists only in this process's memory. A restart starts a new salt (that day's
    /// returning visitors count twice), and several instances would each have their own.
    /// </summary>
    public static VisitorHasher InMemory() => new(new InMemorySaltStore(), SaltSealer.Plain);

    public async ValueTask<string> HashAsync(string siteKey, IPAddress ip, string userAgent, DateTime utcNow, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(utcNow);
        var salt = await GetSaltAsync(day, ct);

        var input = Encoding.UTF8.GetBytes($"{siteKey}\n{NormalizeIp(ip)}\n{userAgent}");
        var mac = HMACSHA256.HashData(salt, input);
        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }

    private async Task<byte[]> GetSaltAsync(DateOnly day, CancellationToken ct)
    {
        if (_cache.TryGetValue(day, out var cached)) return cached;

        var salt = await LoadSaltAsync(day, ct);
        _cache[day] = salt;

        // Forget (in memory and in the store) every earlier salt: only today's is ever used, and once a salt is
        // gone, that day's visitor ids can no longer be recomputed from an IP address and user agent.
        foreach (var key in _cache.Keys)
        {
            if (key < day) _cache.TryRemove(key, out _);
        }
        await salts.PruneAsync(day, ct);

        return salt;
    }

    private async Task<byte[]> LoadSaltAsync(DateOnly day, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Offer a new sealed salt; if another instance (or an earlier run) got there first, use theirs.
            var stored = await salts.GetOrAddAsync(day, sealer.Seal(day, RandomNumberGenerator.GetBytes(32)), ct);
            if (sealer.TryOpen(day, stored, out var salt)) return salt;

            if (attempt > 0)
            {
                throw new InvalidOperationException(
                    $"The stored salt for {day:yyyy-MM-dd} cannot be decrypted with Ingest:SaltKey. " +
                    "Every ingest instance must use the same key.");
            }

            // Sealed under another key (the key was changed) or stored before salts were encrypted. It is useless to
            // us, so replace it; only the visitor ids of earlier today change.
            log?.LogWarning(
                "The stored salt for {Day} was not sealed with the current Ingest:SaltKey; replacing it. " +
                "Visitors seen earlier today will be counted again", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await salts.RemoveAsync(day, stored, ct);
        }
    }

    /// <summary>
    /// IPv4-mapped IPv6 addresses are unmapped; IPv6 addresses are truncated to their /64 prefix, because
    /// clients rotate the lower 64 bits (privacy extensions) and would otherwise look like many visitors.
    /// </summary>
    public static string NormalizeIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString();
    }
}

/// <summary>
/// Encrypts daily salts with AES-256-GCM before they are stored. The key is derived (HKDF-SHA256) from
/// Ingest:SaltKey, which lives only in the ingest configuration, never in the database. The day is authenticated
/// as associated data, so a sealed salt cannot be moved to another day. Layout: version (1) | nonce (12) |
/// ciphertext (32) | tag (16).
/// </summary>
public sealed class SaltSealer
{
    public const int MinKeyLength = 32;

    private const byte Version = 1;
    private const int SaltSize = 32, NonceSize = 12, TagSize = 16;
    private const int SealedSize = 1 + NonceSize + SaltSize + TagSize;

    private readonly byte[]? _key;

    /// <summary>No encryption, for <see cref="VisitorHasher.InMemory"/> only, where nothing leaves the process.</summary>
    internal static SaltSealer Plain { get; } = new();

    private SaltSealer() { }

    public SaltSealer(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinKeyLength)
            throw new ArgumentException($"the salt key must be at least {MinKeyLength} characters", nameof(key));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key), 32,
            info: "fishenv.analytics daily salt v1"u8.ToArray());
    }

    public byte[] Seal(DateOnly day, byte[] salt)
    {
        if (salt.Length != SaltSize) throw new ArgumentException($"a salt is {SaltSize} bytes", nameof(salt));
        if (_key is null) return (byte[])salt.Clone();

        var sealedSalt = new byte[SealedSize];
        sealedSalt[0] = Version;
        var nonce = sealedSalt.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, salt, sealedSalt.AsSpan(1 + NonceSize, SaltSize),
            sealedSalt.AsSpan(1 + NonceSize + SaltSize, TagSize), AssociatedData(day));
        return sealedSalt;
    }

    /// <summary>False if the value was sealed with another key, for another day, or is not a sealed salt.</summary>
    public bool TryOpen(DateOnly day, byte[] sealedSalt, [NotNullWhen(true)] out byte[]? salt)
    {
        salt = null;
        if (_key is null)
        {
            if (sealedSalt.Length != SaltSize) return false;
            salt = sealedSalt;
            return true;
        }
        if (sealedSalt.Length != SealedSize || sealedSalt[0] != Version) return false;

        var plain = new byte[SaltSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedSalt.AsSpan(1, NonceSize), sealedSalt.AsSpan(1 + NonceSize, SaltSize),
                sealedSalt.AsSpan(1 + NonceSize + SaltSize, TagSize), plain, AssociatedData(day));
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        salt = plain;
        return true;
    }

    private static byte[] AssociatedData(DateOnly day) =>
        Encoding.ASCII.GetBytes("daily-salt " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>Salt store kept in this process's memory. Salts are lost on restart.</summary>
public sealed class InMemorySaltStore : ISaltStore
{
    private readonly ConcurrentDictionary<DateOnly, byte[]> _salts = new();

    public Task<byte[]> GetOrAddAsync(DateOnly day, byte[] value, CancellationToken ct) =>
        Task.FromResult(_salts.GetOrAdd(day, value));

    public Task RemoveAsync(DateOnly day, byte[] expected, CancellationToken ct)
    {
        _salts.TryRemove(new KeyValuePair<DateOnly, byte[]>(day, expected));
        return Task.CompletedTask;
    }

    public Task PruneAsync(DateOnly before, CancellationToken ct)
    {
        foreach (var key in _salts.Keys)
        {
            if (key < before) _salts.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stable visitor ids for logged-in users on sites with identify_users: "u_" + HMAC-SHA256(secret, site | user id),
/// truncated. The same user gets the same id every day (and on every device), so visitor counts over several days
/// are real unique users. The raw user id is never stored. The secret lives in the ingest configuration, not in
/// the database, so a database dump alone cannot be matched back to user ids by hashing candidate ids.
/// </summary>
public sealed class UserIdHasher
{
    public const string Prefix = "u_";
    public const int MinSecretLength = 32;

    private readonly byte[]? _key;

    /// <param name="secret">Null or empty disables identification (every event gets the anonymous daily id).</param>
    public UserIdHasher(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        if (secret.Length < MinSecretLength)
            throw new ArgumentException($"the identity secret must be at least {MinSecretLength} characters", nameof(secret));
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public bool IsEnabled => _key is not null;

    public string Hash(long siteId, string userId)
    {
        if (_key is null) throw new InvalidOperationException("no identity secret configured");
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{siteId}\n{userId}"));
        return Prefix + Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }
}
