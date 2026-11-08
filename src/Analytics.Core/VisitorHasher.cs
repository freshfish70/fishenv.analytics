using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Analytics.Core;

/// <summary>
/// Produces an anonymous visitor identifier: HMAC-SHA256(daily salt, site | ip | user agent), truncated.
/// The salt is random and changes every UTC day; the previous day's salt is deleted with the first event of the new
/// day, so identifiers cannot be linked across days and cannot be reversed to an IP address once the salt is gone.
/// Neither the IP nor the user agent is stored.
/// </summary>
public sealed class VisitorHasher(ISaltStore salts)
{
    private readonly ConcurrentDictionary<DateOnly, byte[]> _cache = new();

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

        var salt = await salts.GetOrCreateAsync(day, ct);
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

/// <summary>Salt store for tests and single-process development. Salts are lost on restart.</summary>
public sealed class InMemorySaltStore : ISaltStore
{
    private readonly ConcurrentDictionary<DateOnly, byte[]> _salts = new();

    public Task<byte[]> GetOrCreateAsync(DateOnly day, CancellationToken ct) =>
        Task.FromResult(_salts.GetOrAdd(day, _ => RandomNumberGenerator.GetBytes(32)));

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
