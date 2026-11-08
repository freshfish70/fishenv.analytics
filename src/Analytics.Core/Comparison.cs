namespace Analytics.Core;

/// <summary>Relative changes between a summary and its comparison period.</summary>
/// <remarks>Percentages are null when the previous value is 0. Bounce rate changes in percentage points.</remarks>
public sealed record SummaryChange(
    double? Visitors,
    double? Pageviews,
    double? Events,
    double? Visits,
    double BounceRate,
    double? AvgVisitDurationSeconds)
{
    public static SummaryChange Between(SummaryResult current, SummaryResult previous) => new(
        StatsComparison.PercentChange(current.Visitors, previous.Visitors),
        StatsComparison.PercentChange(current.Pageviews, previous.Pageviews),
        StatsComparison.PercentChange(current.Events, previous.Events),
        StatsComparison.PercentChange(current.Visits, previous.Visits),
        Math.Round((current.BounceRate - previous.BounceRate) * 100, 1),
        StatsComparison.PercentChange(current.AvgVisitDurationSeconds, previous.AvgVisitDurationSeconds));
}

public static class StatsComparison
{
    /// <summary>Change in percent, rounded to one decimal; null when there is nothing to compare with.</summary>
    public static double? PercentChange(double current, double previous) =>
        previous == 0 ? null : Math.Round((current - previous) * 100 / previous, 1);

    /// <summary>
    /// The previous period's rows in the order of <paramref name="current"/>, matched by key; a value that did not
    /// occur in the previous period gets <paramref name="missing"/>. Null keys ("(none)") match each other.
    /// </summary>
    public static List<T> Align<T>(
        IReadOnlyList<T> current, IReadOnlyList<T> previous, Func<T, string?> key, Func<string?, T> missing)
    {
        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        T? nullRow = default;
        var hasNull = false;
        foreach (var row in previous)
        {
            var k = key(row);
            if (k is null)
            {
                if (!hasNull) (nullRow, hasNull) = (row, true);
            }
            else byKey.TryAdd(k, row);
        }

        return current.Select(row =>
        {
            var k = key(row);
            if (k is null) return hasNull ? nullRow! : missing(null);
            return byKey.TryGetValue(k, out var prev) ? prev : missing(k);
        }).ToList();
    }
}
