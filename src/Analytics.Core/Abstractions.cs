namespace Analytics.Core;

/// <summary>Persists processed events. Implemented by the Postgres project.</summary>
public interface IEventStore
{
    /// <summary>
    /// Stores the batch. Rows the database rejects because of their content are skipped (and logged) so they cannot
    /// block the rest; an exception means nothing was stored and the whole batch can be retried.
    /// </summary>
    Task WriteBatchAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct);
}

/// <summary>Read-only view of the registered sites (used by the ingest service).</summary>
public interface ISiteLoader
{
    Task<IReadOnlyList<SiteInfo>> LoadAllAsync(CancellationToken ct);
}

/// <summary>Site management (used by the API service).</summary>
public interface ISiteStore : ISiteLoader
{
    Task<SiteInfo?> GetAsync(long id, CancellationToken ct);

    /// <summary>Returns null if a site with that domain already exists.</summary>
    Task<SiteInfo?> CreateAsync(string domain, string? name, bool identifyUsers, CancellationToken ct);

    /// <summary>Changes the given settings (null = keep). Returns null for an unknown site.</summary>
    Task<SiteInfo?> UpdateAsync(long id, string? name, bool? identifyUsers, CancellationToken ct);

    /// <summary>Deletes the site and all of its events.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct);

    /// <summary>
    /// Deletes events whose site no longer exists. The ingest service keeps accepting a deleted site's events until
    /// its next site refresh, and events.site_id has no foreign key, so those would otherwise stay forever.
    /// Returns the number of deleted events.
    /// </summary>
    Task<int> DeleteOrphanedEventsAsync(CancellationToken ct);
}

/// <summary>Stores the per-day random salts used for visitor hashing.</summary>
public interface ISaltStore
{
    Task<byte[]> GetOrCreateAsync(DateOnly day, CancellationToken ct);

    /// <summary>Deletes salts for days strictly before <paramref name="before"/>.</summary>
    Task PruneAsync(DateOnly before, CancellationToken ct);
}

public interface IApiKeyStore
{
    Task<ApiKeyInfo?> FindAsync(byte[] keyHash, CancellationToken ct);
    Task<long> CreateAsync(string name, ApiKeyScope scope, byte[] keyHash, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyListItem>> ListAsync(CancellationToken ct);
    Task<bool> DeleteAsync(long id, CancellationToken ct);
}

public interface IStatsQuery
{
    Task<SummaryResult> SummaryAsync(StatsRequest request, CancellationToken ct);
    Task<IReadOnlyList<BreakdownRow>> BreakdownAsync(StatsRequest request, string by, int limit, CancellationToken ct);
    Task<IReadOnlyList<TimeseriesPoint>> TimeseriesAsync(StatsRequest request, string interval, CancellationToken ct);

    /// <summary>Breakdown by a visit-level dimension (entry_page, exit_page).</summary>
    Task<IReadOnlyList<SessionBreakdownRow>> SessionBreakdownAsync(StatsRequest request, string by, int limit, CancellationToken ct);

    /// <summary>Usage per custom event name.</summary>
    Task<IReadOnlyList<EventStatsRow>> EventsAsync(StatsRequest request, int limit, CancellationToken ct);

    /// <summary>The property keys carried by the matching events, most used first.</summary>
    Task<IReadOnlyList<PropKeyRow>> PropKeysAsync(StatsRequest request, CancellationToken ct);
}
