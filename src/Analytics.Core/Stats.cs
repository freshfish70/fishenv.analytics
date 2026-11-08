using System.Text.RegularExpressions;

namespace Analytics.Core;

public enum FilterOp
{
    /// <summary>Equal. Several on the same dimension match any of them.</summary>
    Is,
    /// <summary>Not equal (an unset value counts as "not equal").</summary>
    IsNot,
    /// <summary>Case-insensitive substring. Several on the same dimension match any of them.</summary>
    Contains,
    /// <summary>Case-insensitive, does not contain the substring (an unset value counts as "does not contain").</summary>
    NotContains,
    /// <summary>Case-sensitive prefix. Several on the same dimension match any of them.</summary>
    Prefix,
}

/// <summary>
/// A single filter. <see cref="Value"/> null means "is not set" (SQL NULL) and is only valid with
/// <see cref="FilterOp.Is"/> and <see cref="FilterOp.IsNot"/>.
/// </summary>
public sealed record StatsFilter(string Dimension, string? Value, FilterOp Op = FilterOp.Is);

public sealed record StatsRequest(
    long SiteId,
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<StatsFilter> Filters,
    string TimeZone = "UTC");

/// <summary>The dimensions events can be filtered and grouped by, mapped to their database columns.</summary>
public static partial class StatsDimensions
{
    public const string PropPrefix = "prop.";

    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["path"] = "path",
        ["referrer"] = "referrer",
        ["country"] = "country",
        ["browser"] = "browser",
        ["os"] = "os",
        ["device"] = "device",
        ["utm_source"] = "utm_source",
        ["utm_medium"] = "utm_medium",
        ["utm_campaign"] = "utm_campaign",
        ["event"] = "type",
        // Visitors identified by a user id (sites with identify_users) have ids starting with "u_"; the daily
        // anonymous hashes are lowercase hex and never do.
        ["identified"] = "(CASE WHEN left(visitor, 2) = 'u_' THEN 'yes' ELSE 'no' END)",
    };

    /// <summary>Dimensions of a whole visit, computed at query time from the visit's pageviews.</summary>
    private static readonly Dictionary<string, string> SessionColumns = new(StringComparer.Ordinal)
    {
        ["entry_page"] = "entry_page",
        ["exit_page"] = "exit_page",
    };

    [GeneratedRegex(@"^[\w\-.]{1,40}$")]
    private static partial Regex PropKeyRegex();

    public static IEnumerable<string> Names => Columns.Keys.Concat(SessionColumns.Keys);

    /// <summary>The SQL expression of an event-level dimension (a column of <c>events</c>).</summary>
    public static bool TryGetColumn(string dimension, out string column) =>
        Columns.TryGetValue(dimension, out column!);

    /// <summary>The column of a visit-level dimension (entry_page, exit_page) in the sessions CTE.</summary>
    public static bool TryGetSessionColumn(string dimension, out string column) =>
        SessionColumns.TryGetValue(dimension, out column!);

    public static bool IsSessionDimension(string dimension) => SessionColumns.ContainsKey(dimension);

    /// <summary>True for "prop.&lt;key&gt;" dimensions with a valid key, which address a custom event property.</summary>
    public static bool TryGetPropKey(string dimension, out string key)
    {
        key = "";
        if (!dimension.StartsWith(PropPrefix, StringComparison.Ordinal)) return false;
        var k = dimension[PropPrefix.Length..];
        if (!PropKeyRegex().IsMatch(k)) return false;
        key = k;
        return true;
    }

    public static bool IsValid(string dimension) =>
        Columns.ContainsKey(dimension) || SessionColumns.ContainsKey(dimension) || TryGetPropKey(dimension, out _);
}

public static class StatsFilterParser
{
    public const int MaxFilters = 10;
    public const int MaxValueLength = 500;

    private static readonly Dictionary<string, FilterOp> Operators = new(StringComparer.Ordinal)
    {
        ["is"] = FilterOp.Is,
        ["not"] = FilterOp.IsNot,
        ["contains"] = FilterOp.Contains,
        ["not_contains"] = FilterOp.NotContains,
        ["prefix"] = FilterOp.Prefix,
    };

    public static IEnumerable<string> OperatorNames => Operators.Keys;

    /// <summary>
    /// Extracts filters from query-string pairs. Recognized keys are the dimension names (see
    /// <see cref="StatsDimensions.Names"/>) and "prop.&lt;key&gt;", optionally followed by an operator in brackets:
    /// <c>path[contains]=blog</c>, <c>country[not]=NO</c>. Everything else is ignored.
    /// The value "(none)" matches events where the dimension is not set (with "is" and "not" only).
    /// </summary>
    public static bool TryParse(
        IEnumerable<KeyValuePair<string, string>> query,
        out List<StatsFilter> filters,
        out string? error)
    {
        filters = [];
        error = null;

        foreach (var (rawKey, raw) in query)
        {
            var key = rawKey;
            string? opName = null;
            var bracket = key.IndexOf('[');
            if (bracket > 0 && key.EndsWith(']'))
            {
                opName = key[(bracket + 1)..^1].ToLowerInvariant();
                key = key[..bracket];
            }

            var lower = key.ToLowerInvariant();
            string dimension;
            if (StatsDimensions.TryGetColumn(lower, out _) || StatsDimensions.IsSessionDimension(lower))
            {
                dimension = lower;
            }
            else if (lower.StartsWith(StatsDimensions.PropPrefix, StringComparison.Ordinal))
            {
                // Property keys are case-sensitive, so keep the original casing after the prefix.
                dimension = StatsDimensions.PropPrefix + key[StatsDimensions.PropPrefix.Length..];
                if (!StatsDimensions.TryGetPropKey(dimension, out _))
                {
                    error = $"invalid property filter '{rawKey}'";
                    return false;
                }
            }
            else
            {
                continue;
            }

            var op = FilterOp.Is;
            if (opName is not null && !Operators.TryGetValue(opName, out op))
            {
                error = $"unknown filter operator in '{rawKey}' (use one of: {string.Join(", ", Operators.Keys)})";
                return false;
            }

            if (raw.Length == 0) continue;
            if (raw.Length > MaxValueLength)
            {
                error = $"filter '{rawKey}' is too long";
                return false;
            }

            var value = raw == "(none)" ? null : raw;
            if (value is null && op is not (FilterOp.Is or FilterOp.IsNot))
            {
                error = $"'(none)' can only be used with the is and not operators ('{rawKey}')";
                return false;
            }
            if (dimension == "country" && value is not null) value = value.ToUpperInvariant();

            filters.Add(new StatsFilter(dimension, value, op));
            if (filters.Count > MaxFilters)
            {
                error = "too many filters";
                return false;
            }
        }

        return true;
    }
}
