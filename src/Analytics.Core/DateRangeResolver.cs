using System.Globalization;
using System.Text.RegularExpressions;

namespace Analytics.Core;

/// <summary>
/// Turns the query-string time parameters into a half-open UTC range [from, to).
/// Calendar boundaries (today, month, ...) are evaluated in the caller's time zone.
/// </summary>
public static partial class DateRangeResolver
{
    public const int MaxRangeDays = 1096; // about three years

    [GeneratedRegex(@"^(\d{1,4})d$")]
    private static partial Regex DaysRegex();

    public static bool TryResolve(
        string? period, string? from, string? to,
        TimeZoneInfo tz, DateTime utcNow,
        out DateTime fromUtc, out DateTime toUtc, out string? error)
    {
        fromUtc = toUtc = default;
        error = null;

        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz).Date;

        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            if (string.IsNullOrWhiteSpace(from))
            {
                error = "'from' is required when 'to' is given";
                return false;
            }
            if (!TryParseBound(from, tz, endBound: false, out fromUtc))
            {
                error = "invalid 'from' (use yyyy-MM-dd or an ISO 8601 timestamp)";
                return false;
            }
            if (string.IsNullOrWhiteSpace(to)) toUtc = ToUtc(today.AddDays(1), tz);
            else if (!TryParseBound(to, tz, endBound: true, out toUtc))
            {
                error = "invalid 'to' (use yyyy-MM-dd or an ISO 8601 timestamp)";
                return false;
            }
        }
        else
        {
            DateTime localFrom, localTo;
            var p = string.IsNullOrWhiteSpace(period) ? "30d" : period.Trim().ToLowerInvariant();
            var m = DaysRegex().Match(p);

            if (m.Success)
            {
                var days = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (days < 1)
                {
                    error = "period must be at least 1d";
                    return false;
                }
                localFrom = today.AddDays(-(days - 1));
                localTo = today.AddDays(1);
            }
            else
            {
                switch (p)
                {
                    case "today":
                    case "day":
                        localFrom = today;
                        localTo = today.AddDays(1);
                        break;
                    case "month":
                        localFrom = new DateTime(today.Year, today.Month, 1);
                        localTo = localFrom.AddMonths(1);
                        break;
                    case "12mo":
                        localFrom = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
                        localTo = new DateTime(today.Year, today.Month, 1).AddMonths(1);
                        break;
                    case "year":
                        localFrom = new DateTime(today.Year, 1, 1);
                        localTo = localFrom.AddYears(1);
                        break;
                    default:
                        error = "unknown period (use today, Nd such as 7d, month, 12mo or year)";
                        return false;
                }
            }

            fromUtc = ToUtc(localFrom, tz);
            toUtc = ToUtc(localTo, tz);
        }

        if (toUtc <= fromUtc)
        {
            error = "'to' must be after 'from'";
            return false;
        }
        if ((toUtc - fromUtc).TotalDays > MaxRangeDays)
        {
            error = $"range is limited to {MaxRangeDays} days";
            return false;
        }
        return true;
    }

    public static readonly IReadOnlyList<string> CompareModes = ["previous_period", "year_over_year", "custom"];

    /// <summary>
    /// The range to compare [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) with:
    /// <list type="bullet">
    /// <item><c>previous_period</c>: the period before. Calendar periods step by their unit (month: the previous
    /// month, year: the previous year, 7d: the 7 days before); a custom from/to range by its own length.</item>
    /// <item><c>year_over_year</c>: the same range one calendar year earlier.</item>
    /// <item><c>custom</c>: <paramref name="compareFrom"/> to <paramref name="compareTo"/> (same formats as from/to).</item>
    /// </list>
    /// When the current range has not ended yet (this month), <see cref="ComparisonRange.AggregateToUtc"/> stops the
    /// comparison at the same point in time, so totals compare like with like; timeseries use the whole range.
    /// A blank <paramref name="compare"/> gives no comparison (<paramref name="range"/> null) and returns true.
    /// </summary>
    public static bool TryResolveComparison(
        string? compare, string? compareFrom, string? compareTo,
        string? period, bool customRange,
        TimeZoneInfo tz, DateTime utcNow, DateTime fromUtc, DateTime toUtc,
        out ComparisonRange? range, out string? error)
    {
        range = null;
        error = null;
        if (string.IsNullOrWhiteSpace(compare)) return true;

        Func<DateTime, DateTime> shift;
        switch (compare.Trim().ToLowerInvariant())
        {
            case "previous_period":
                if (customRange)
                {
                    var length = toUtc - fromUtc;
                    shift = d => d - length;
                }
                else if (!TryPeriodShift(period, tz, out shift))
                {
                    error = "unknown period";
                    return false;
                }
                break;

            case "year_over_year":
                shift = d => ShiftLocal(d, tz, l => l.AddYears(-1));
                break;

            case "custom":
                if (string.IsNullOrWhiteSpace(compareFrom) || string.IsNullOrWhiteSpace(compareTo))
                {
                    error = "compare=custom needs compare_from and compare_to";
                    return false;
                }
                if (!TryParseBound(compareFrom, tz, endBound: false, out var cf) ||
                    !TryParseBound(compareTo, tz, endBound: true, out var ct))
                {
                    error = "invalid 'compare_from' or 'compare_to' (use yyyy-MM-dd or an ISO 8601 timestamp)";
                    return false;
                }
                if (ct <= cf)
                {
                    error = "'compare_to' must be after 'compare_from'";
                    return false;
                }
                if ((ct - cf).TotalDays > MaxRangeDays)
                {
                    error = $"range is limited to {MaxRangeDays} days";
                    return false;
                }
                range = new ComparisonRange(cf, ct, ct);
                return true;

            default:
                error = $"'compare' must be one of: {string.Join(", ", CompareModes)}";
                return false;
        }

        var prevFrom = shift(fromUtc);
        var prevTo = shift(toUtc);
        var aggregateTo = utcNow > fromUtc && utcNow < toUtc ? shift(utcNow) : prevTo;
        range = new ComparisonRange(prevFrom, prevTo, aggregateTo);
        return true;
    }

    private static bool TryPeriodShift(string? period, TimeZoneInfo tz, out Func<DateTime, DateTime> shift)
    {
        var p = string.IsNullOrWhiteSpace(period) ? "30d" : period.Trim().ToLowerInvariant();
        var m = DaysRegex().Match(p);
        if (m.Success)
        {
            var days = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            shift = d => ShiftLocal(d, tz, l => l.AddDays(-days));
            return true;
        }

        Func<DateTime, DateTime>? local = p switch
        {
            "today" or "day" => l => l.AddDays(-1),
            "month" => l => l.AddMonths(-1),
            "12mo" => l => l.AddMonths(-12),
            "year" => l => l.AddYears(-1),
            _ => null,
        };
        shift = local is null ? (d => d) : (d => ShiftLocal(d, tz, local));
        return local is not null;
    }

    /// <summary>Moves a UTC instant by a calendar step in local time, so day boundaries survive DST changes.</summary>
    private static DateTime ShiftLocal(DateTime utc, TimeZoneInfo tz, Func<DateTime, DateTime> step)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return ToUtc(step(local), tz);
    }

    private static bool TryParseBound(string value, TimeZoneInfo tz, bool endBound, out DateTime utc)
    {
        utc = default;
        value = value.Trim();

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            // A plain date is a calendar day in the caller's time zone; as an end bound it includes that whole day.
            var local = date.ToDateTime(TimeOnly.MinValue);
            utc = ToUtc(endBound ? local.AddDays(1) : local, tz);
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        return false;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1); // DST gap
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}

/// <summary>A comparison range. <see cref="AggregateToUtc"/> is the end used for totals (see TryResolveComparison).</summary>
public sealed record ComparisonRange(DateTime FromUtc, DateTime ToUtc, DateTime AggregateToUtc);
