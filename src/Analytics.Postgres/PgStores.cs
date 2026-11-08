using System.Security.Cryptography;
using Analytics.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Analytics.Postgres;

/// <summary>
/// Event batches bypass EF Core on purpose: binary COPY is far cheaper than INSERTs through the change tracker,
/// and the ingest role only needs INSERT on <c>events</c> for it.
/// </summary>
internal sealed class PgEventStore(NpgsqlDataSource ds, ILogger<PgEventStore> log) : IEventStore
{
    private const string CopySql =
        "COPY events (ts, site_id, visitor, type, path, referrer, country, browser, os, device, " +
        "utm_source, utm_medium, utm_campaign, props) FROM STDIN (FORMAT BINARY)";

    public async Task WriteBatchAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await CopyAsync(conn, batch, ct);
        }
        catch (PostgresException ex) when (IsBadRow(ex) && batch.Count > 1)
        {
            // One unstorable row must not cost the rest of the batch.
            log.LogWarning(ex, "Batch of {Count} events was rejected; writing them one by one", batch.Count);
            await WriteRowByRowAsync(batch, ct);
        }
        catch (PostgresException ex) when (IsBadRow(ex))
        {
            log.LogWarning(ex, "Dropping 1 event that the database rejected");
        }
    }

    /// <summary>
    /// Writes each row behind its own savepoint in a single transaction and skips the rows the database rejects.
    /// Any other failure rolls everything back, so the caller can retry the whole batch without duplicates.
    /// </summary>
    private async Task WriteRowByRowAsync(IReadOnlyList<EventRecord> batch, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var dropped = 0;
        var row = new EventRecord[1];

        foreach (var e in batch)
        {
            row[0] = e;
            await tx.SaveAsync("row", ct);
            try
            {
                await CopyAsync(conn, row, ct);
                await tx.ReleaseAsync("row", ct);
            }
            catch (PostgresException ex) when (IsBadRow(ex))
            {
                await tx.RollbackAsync("row", ct);
                dropped++;
            }
        }

        await tx.CommitAsync(ct);
        if (dropped > 0) log.LogWarning("Dropped {Dropped} of {Count} events that the database rejected", dropped, batch.Count);
    }

    /// <summary>
    /// Errors caused by the row's content (class 22 data exception, 23 constraint violation, 54 program limit
    /// such as an oversized index entry). Retrying them can never succeed; everything else is worth a retry.
    /// </summary>
    private static bool IsBadRow(PostgresException ex) =>
        ex.SqlState.StartsWith("22", StringComparison.Ordinal) ||
        ex.SqlState.StartsWith("23", StringComparison.Ordinal) ||
        ex.SqlState.StartsWith("54", StringComparison.Ordinal);

    private static async Task CopyAsync(NpgsqlConnection conn, IReadOnlyList<EventRecord> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(CopySql, ct);

        foreach (var e in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(DateTime.SpecifyKind(e.Ts, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(e.SiteId, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(e.Visitor, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Type, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Path, NpgsqlDbType.Text, ct);
            await WriteTextOrNull(writer, e.Referrer, ct);
            await WriteTextOrNull(writer, e.Country, ct);
            await WriteTextOrNull(writer, e.Browser, ct);
            await WriteTextOrNull(writer, e.Os, ct);
            await WriteTextOrNull(writer, e.Device, ct);
            await WriteTextOrNull(writer, e.UtmSource, ct);
            await WriteTextOrNull(writer, e.UtmMedium, ct);
            await WriteTextOrNull(writer, e.UtmCampaign, ct);

            if (e.PropsJson is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(e.PropsJson, NpgsqlDbType.Jsonb, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static async Task WriteTextOrNull(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value is null) await writer.WriteNullAsync(ct);
        else await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }
}

internal sealed class PgSiteStore(IDbContextFactory<AnalyticsDbContext> factory) : ISiteStore
{
    public async Task<IReadOnlyList<SiteInfo>> LoadAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Sites.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new SiteInfo(s.Id, s.Domain, s.Name, s.CreatedAt, s.IdentifyUsers))
            .ToListAsync(ct);
    }

    public async Task<SiteInfo?> GetAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Sites.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new SiteInfo(s.Id, s.Domain, s.Name, s.CreatedAt, s.IdentifyUsers))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<SiteInfo?> CreateAsync(string domain, string? name, bool identifyUsers, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // The common duplicate case is answered without a failing INSERT (which EF Core would log as an error);
        // the unique index still decides when two requests race.
        if (await db.Sites.AnyAsync(s => s.Domain == domain, ct)) return null;

        var site = new SiteEntity { Domain = domain, Name = name, IdentifyUsers = identifyUsers };
        db.Sites.Add(site);

        try
        {
            await db.SaveChangesAsync(ct); // id and created_at come back via RETURNING
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                           })
        {
            return null;
        }

        return new SiteInfo(site.Id, site.Domain, site.Name, site.CreatedAt, site.IdentifyUsers);
    }

    public async Task<SiteInfo?> UpdateAsync(long id, string? name, bool? identifyUsers, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (site is null) return null;

        if (name is not null) site.Name = name;
        if (identifyUsers is { } identify) site.IdentifyUsers = identify;
        await db.SaveChangesAsync(ct);

        return new SiteInfo(site.Id, site.Domain, site.Name, site.CreatedAt, site.IdentifyUsers);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Events.Where(e => e.SiteId == id).ExecuteDeleteAsync(ct);
        var deleted = await db.Sites.Where(s => s.Id == id).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted > 0;
    }

    public async Task<int> DeleteOrphanedEventsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        return await db.Events
            .Where(e => !db.Sites.Any(s => s.Id == e.SiteId))
            .ExecuteDeleteAsync(ct);
    }
}

internal sealed class PgSaltStore(IDbContextFactory<AnalyticsDbContext> factory) : ISaltStore
{
    public async Task<byte[]> GetOrCreateAsync(DateOnly day, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // EF Core has no upsert. ON CONFLICT keeps this race-free when several ingest instances start a new day
        // at once; the interpolated values become parameters.
        var salt = RandomNumberGenerator.GetBytes(32);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO daily_salts (day, salt) VALUES ({day}, {salt}) ON CONFLICT (day) DO NOTHING", ct);

        return await db.DailySalts.AsNoTracking()
                   .Where(s => s.Day == day)
                   .Select(s => s.Salt)
                   .SingleOrDefaultAsync(ct)
               ?? throw new InvalidOperationException($"No salt stored for {day}");
    }

    public async Task PruneAsync(DateOnly before, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.DailySalts.Where(s => s.Day < before).ExecuteDeleteAsync(ct);
    }
}

internal sealed class PgApiKeyStore(IDbContextFactory<AnalyticsDbContext> factory, TimeProvider time) : IApiKeyStore
{
    public async Task<ApiKeyInfo?> FindAsync(byte[] keyHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var key = await db.ApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == keyHash)
            .Select(k => new { k.Id, k.Name, k.Scope })
            .SingleOrDefaultAsync(ct);
        if (key is null) return null;

        var now = time.GetUtcNow().UtcDateTime;
        await db.ApiKeys.Where(k => k.Id == key.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);

        return new ApiKeyInfo(key.Id, key.Name, key.Scope == "admin" ? ApiKeyScope.Admin : ApiKeyScope.Read);
    }

    public async Task<long> CreateAsync(string name, ApiKeyScope scope, byte[] keyHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = new ApiKeyEntity
        {
            Name = name,
            Scope = scope == ApiKeyScope.Admin ? "admin" : "read",
            KeyHash = keyHash,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return key.Id;
    }

    public async Task<IReadOnlyList<ApiKeyListItem>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.AsNoTracking()
            .OrderBy(k => k.Id)
            .Select(k => new ApiKeyListItem(k.Id, k.Name, k.Scope, k.CreatedAt, k.LastUsedAt))
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
