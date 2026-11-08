using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Analytics.Core;

/// <summary>
/// Makes client-supplied strings safe to store: removes control characters (Postgres text cannot hold NUL),
/// drops lone surrogates (which cannot be encoded as UTF-8) and truncates without splitting a surrogate pair.
/// </summary>
public static class TextSanitizer
{
    public static string? Clean(string? s, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        var sb = new StringBuilder(Math.Min(s.Length, maxLength));
        for (var i = 0; i < s.Length && sb.Length < maxLength; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    if (sb.Length + 2 > maxLength) break;
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                // otherwise: lone high surrogate, drop it
            }
            else if (char.IsLowSurrogate(c))
            {
                // lone low surrogate, drop it
            }
            else if (c < 0x20 || c == 0x7f)
            {
                // control character, drop it
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}

public static class ApiKeys
{
    public const string Prefix = "an_";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Base64Url.EncodeToString(bytes);
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

public static class SiteDomain
{
    /// <summary>
    /// Normalizes a site identifier to a bare lower-case host name without scheme, port, path or leading "www.".
    /// Returns null if the input is not a plausible host name.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        if (s.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
            s = uri.Host;
        }
        else
        {
            var slash = s.IndexOf('/');
            if (slash >= 0) s = s[..slash];
            var colon = s.LastIndexOf(':');
            if (colon >= 0) s = s[..colon];
        }

        s = s.TrimEnd('.').ToLowerInvariant();
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        if (s.Length is 0 or > 253) return null;

        return Uri.CheckHostName(s) is UriHostNameType.Dns or UriHostNameType.IPv4 ? s : null;
    }

    /// <summary>True if <paramref name="host"/> is the site's domain or one of its subdomains.</summary>
    public static bool HostMatchesSite(string host, string siteDomain)
    {
        var h = Normalize(host);
        return h is not null &&
               (h.Equals(siteDomain, StringComparison.Ordinal) ||
                h.EndsWith("." + siteDomain, StringComparison.Ordinal));
    }
}
