using System.Text.RegularExpressions;

namespace Analytics.Core;

public readonly record struct UserAgentInfo(string Browser, string Os, string Device, bool IsBot);

/// <summary>
/// Small heuristic user-agent classifier. It is intentionally simple: it covers the mainstream browsers and
/// operating systems and flags obvious bots. The raw user agent is never stored.
/// </summary>
public static partial class UserAgentParser
{
    [GeneratedRegex(
        @"bot|crawl|spider|slurp|facebookexternalhit|embedly|preview|headless|lighthouse|pingdom|uptime|monitor|" +
        @"curl/|wget/|python-requests|python-urllib|go-http-client|java/|okhttp|libwww|httpclient|scrapy|phantomjs|selenium",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BotRegex();

    public static UserAgentInfo Parse(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return new("Unknown", "Unknown", "desktop", true);

        var isBot = BotRegex().IsMatch(ua);
        return new UserAgentInfo(DetectBrowser(ua), DetectOs(ua), DetectDevice(ua), isBot);
    }

    private static bool Has(string ua, string token) => ua.Contains(token, StringComparison.Ordinal);

    private static string DetectBrowser(string ua)
    {
        if (Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/")) return "Edge";
        if (Has(ua, "OPR/") || Has(ua, "Opera")) return "Opera";
        if (Has(ua, "SamsungBrowser/")) return "Samsung Internet";
        if (Has(ua, "Vivaldi/")) return "Vivaldi";
        if (Has(ua, "DuckDuckGo/")) return "DuckDuckGo";
        if (Has(ua, "Firefox/") || Has(ua, "FxiOS/")) return "Firefox";
        if (Has(ua, "CriOS/") || Has(ua, "Chrome/")) return "Chrome";
        if (Has(ua, "Safari/") && Has(ua, "Version/")) return "Safari";
        if (Has(ua, "MSIE ") || Has(ua, "Trident/")) return "Internet Explorer";
        return "Other";
    }

    private static string DetectOs(string ua)
    {
        if (Has(ua, "Windows Phone")) return "Windows Phone";
        if (Has(ua, "Windows")) return "Windows";
        // iOS user agents also contain "like Mac OS X", so they must be checked first.
        if (Has(ua, "iPhone") || Has(ua, "iPad") || Has(ua, "iPod")) return "iOS";
        if (Has(ua, "Android")) return "Android";
        if (Has(ua, "CrOS")) return "ChromeOS";
        if (Has(ua, "Mac OS X") || Has(ua, "Macintosh")) return "macOS";
        if (Has(ua, "Linux") || Has(ua, "X11")) return "Linux";
        return "Other";
    }

    private static string DetectDevice(string ua)
    {
        if (Has(ua, "iPad") || Has(ua, "Tablet") || (Has(ua, "Android") && !Has(ua, "Mobile"))) return "tablet";
        if (Has(ua, "Mobi") || Has(ua, "iPhone") || Has(ua, "iPod")) return "mobile";
        return "desktop";
    }
}
