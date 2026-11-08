namespace Analytics.Core;

/// <summary>A fully processed event, ready to be stored. Contains no IP address and no raw user agent.</summary>
public sealed record EventRecord(
    DateTime Ts,
    long SiteId,
    string Visitor,
    string Type,
    string Path,
    string? Referrer,
    string? Country,
    string? Browser,
    string? Os,
    string? Device,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? PropsJson);

/// <param name="IdentifyUsers">
/// Accept a user id from the tracker and use a stable, keyed hash of it as the visitor id (for internal sites with
/// logged-in users). Off by default: public sites only ever get the daily anonymous hash.
/// </param>
public sealed record SiteInfo(long Id, string Domain, string? Name, DateTime CreatedAt, bool IdentifyUsers);

public enum ApiKeyScope
{
    Read = 0,
    Admin = 1,
}

public sealed record ApiKeyInfo(long Id, string Name, ApiKeyScope Scope);

/// <summary>An API key as listed by the admin API (the key itself is never stored, only its hash).</summary>
public sealed record ApiKeyListItem(long Id, string Name, string Scope, DateTime CreatedAt, DateTime? LastUsedAt);

public sealed record SummaryResult(
    long Visitors,
    long Pageviews,
    long Events,
    long Visits,
    double BounceRate,
    double AvgVisitDurationSeconds);

/// <param name="ConversionRate">
/// Only when the request filters on <c>event</c>: the share of this row's visitors who triggered a matching event.
/// </param>
public sealed record BreakdownRow(string? Value, long Visitors, long Pageviews, long Events, double? ConversionRate = null);

/// <summary>A row of a breakdown by a visit-level dimension (entry_page, exit_page).</summary>
public sealed record SessionBreakdownRow(
    string? Value,
    long Visitors,
    long Visits,
    long Pageviews,
    double BounceRate,
    double AvgVisitDurationSeconds);

/// <summary>Usage of one custom event name.</summary>
/// <param name="Visits">Visits that contained the event at least once.</param>
/// <param name="ConversionRate">Visitors who triggered it, divided by all visitors matching the other filters.</param>
/// <param name="EventsPerVisitor">Average number of times a visitor who used it triggered it.</param>
public sealed record EventStatsRow(
    string Name,
    long Visitors,
    long Events,
    long Visits,
    double ConversionRate,
    double EventsPerVisitor);

public sealed record PropKeyRow(string Key, long Events);

public sealed record TimeseriesPoint(string Bucket, long Visitors, long Pageviews, long Events);
