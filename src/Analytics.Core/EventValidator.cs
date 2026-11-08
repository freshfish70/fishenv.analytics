using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Analytics.Core;

/// <summary>The JSON body accepted by POST /api/event.</summary>
public sealed class EventRequest
{
    public string? Site { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public JsonElement Props { get; set; }

    /// <summary>Logged-in user id. Only used for sites with identify_users; never stored as such.</summary>
    public string? User { get; set; }
}

public sealed record ValidatedEvent(
    string SiteDomain,
    string Type,
    string Path,
    string? Referrer,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? PropsJson,
    string? UserId = null);

public static partial class EventValidator
{
    public const int MaxPathLength = 500;
    public const int MaxProps = 10;
    public const int MaxPropsJsonBytes = 2048;
    public const int MaxUserIdLength = 256;

    [GeneratedRegex(@"^[\w\-. :]{1,64}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[\w\-.]{1,40}$")]
    private static partial Regex PropKeyRegex();

    public static bool TryValidate(EventRequest req, out ValidatedEvent? result, out string? error)
    {
        result = null;
        error = null;

        var site = SiteDomain.Normalize(req.Site);
        if (site is null)
        {
            error = "site is required";
            return false;
        }

        string type;
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            type = "pageview";
        }
        else
        {
            var name = req.Name.Trim();
            if (!NameRegex().IsMatch(name))
            {
                error = "invalid event name";
                return false;
            }
            type = name.Equals("pageview", StringComparison.OrdinalIgnoreCase) ? "pageview" : name;
        }

        if (!TryParseUrl(req.Url, out var uri))
        {
            error = "url is required";
            return false;
        }

        var path = TextSanitizer.Clean(DecodePath(uri.AbsolutePath), MaxPathLength) ?? "/";
        if (path[0] != '/') path = "/" + path;

        string? referrer = null;
        if (!string.IsNullOrWhiteSpace(req.Referrer) &&
            req.Referrer.Length <= 2048 &&
            Uri.TryCreate(req.Referrer, UriKind.Absolute, out var refUri) &&
            refUri.Scheme is "http" or "https")
        {
            var host = SiteDomain.Normalize(refUri.Host);
            // Navigation within the same site is not a referrer.
            if (host is not null && !SiteDomain.HostMatchesSite(host, site))
            {
                referrer = TextSanitizer.Clean(host, 255);
            }
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        string? Utm(string key) =>
            query.TryGetValue(key, out var v) ? TextSanitizer.Clean(v.ToString(), 100) : null;

        result = new ValidatedEvent(
            site, type, path, referrer,
            Utm("utm_source"), Utm("utm_medium"), Utm("utm_campaign"),
            BuildProps(req.Props),
            // An over-long id is ignored rather than truncated: truncating could merge different users.
            req.User is { Length: <= MaxUserIdLength } user ? TextSanitizer.Clean(user.Trim(), MaxUserIdLength) : null);
        return true;
    }

    /// <summary>
    /// Uri.AbsolutePath is percent-encoded ("/bl%C3%A5b%C3%A6r"). Decode it per segment so paths are stored as
    /// people read and filter them ("/blåbær"); an encoded slash inside a segment stays "%2F" so segments don't merge.
    /// Invalid escape sequences are left as they are.
    /// </summary>
    internal static string DecodePath(string absolutePath)
    {
        if (!absolutePath.Contains('%')) return absolutePath;
        var segments = absolutePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Contains('%')) segments[i] = Uri.UnescapeDataString(segments[i]).Replace("/", "%2F");
        }
        return string.Join('/', segments);
    }

    private static bool TryParseUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;

        if (url[0] == '/')
        {
            return Uri.TryCreate("http://placeholder.invalid" + url, UriKind.Absolute, out uri!);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";
    }

    /// <summary>
    /// Keeps at most <see cref="MaxProps"/> flat properties with string/number/boolean values.
    /// Anything else is silently dropped. Returns null if nothing is left.
    /// </summary>
    internal static string? BuildProps(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object) return null;

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            var count = 0;
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (var p in props.EnumerateObject())
                {
                    if (count >= MaxProps) break;
                    if (!PropKeyRegex().IsMatch(p.Name)) continue;

                    switch (p.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            var s = TextSanitizer.Clean(p.Value.GetString(), 255);
                            if (s is null) continue;
                            w.WriteString(p.Name, s);
                            break;
                        case JsonValueKind.Number:
                            // Re-serialize instead of copying the raw text: JSON allows numbers such as 1e1000000
                            // that Postgres' numeric type (and so jsonb) rejects, which would fail the whole COPY.
                            if (p.Value.TryGetInt64(out var l)) w.WriteNumber(p.Name, l);
                            else if (p.Value.TryGetDouble(out var d) && double.IsFinite(d)) w.WriteNumber(p.Name, d);
                            else continue;
                            break;
                        case JsonValueKind.True:
                            w.WriteBoolean(p.Name, true);
                            break;
                        case JsonValueKind.False:
                            w.WriteBoolean(p.Name, false);
                            break;
                        default:
                            continue;
                    }
                    count++;
                }
                w.WriteEndObject();
            }

            if (count == 0 || buffer.WrittenCount > MaxPropsJsonBytes) return null;
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
