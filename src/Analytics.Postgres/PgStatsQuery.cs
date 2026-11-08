using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using Analytics.Core;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Analytics.Postgres;

/// <summary>
/// Executes the queries produced by <see cref="StatsSqlBuilder"/> through EF Core's raw SQL API. The session
/// logic needs window functions that LINQ cannot express, so the SQL stays hand-written; every value still
/// travels as a parameter.
/// </summary>
internal sealed partial class PgStatsQuery(IDbContextFactory<AnalyticsDbContext> factory) : IStatsQuery
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    public async Task<SummaryResult> SummaryAsync(StatsRequest request, CancellationToken ct)
    {
        var r = (await RunAsync<SummaryRow>(StatsSqlBuilder.Summary(request), ct)).Single();
        return new SummaryResult(r.Visitors, r.Pageviews, r.Events, r.Visits, r.BounceRate, r.AvgDuration);
    }

    public async Task<IReadOnlyList<BreakdownRow>> BreakdownAsync(
        StatsRequest request, string by, int limit, CancellationToken ct)
    {
        var rows = await RunAsync<BreakdownSqlRow>(StatsSqlBuilder.Breakdown(request, by, limit), ct);
        return rows.Select(r => new BreakdownRow(
                r.Dim, r.Visitors, r.Pageviews, r.Events,
                r.TotalVisitors is { } total ? Ratio(r.Visitors, total) : null))
            .ToList();
    }

    public async Task<IReadOnlyList<SessionBreakdownRow>> SessionBreakdownAsync(
        StatsRequest request, string by, int limit, CancellationToken ct)
    {
        var rows = await RunAsync<SessionBreakdownSqlRow>(StatsSqlBuilder.SessionBreakdown(request, by, limit), ct);
        return rows.Select(r => new SessionBreakdownRow(r.Dim, r.Visitors, r.Visits, r.Pageviews, r.BounceRate, r.AvgDuration))
            .ToList();
    }

    public async Task<IReadOnlyList<EventStatsRow>> EventsAsync(StatsRequest request, int limit, CancellationToken ct)
    {
        var rows = await RunAsync<EventsSqlRow>(StatsSqlBuilder.Events(request, limit), ct);
        return rows.Select(r => new EventStatsRow(
                r.Name, r.Visitors, r.Events, r.Visits,
                Ratio(r.Visitors, r.TotalVisitors),
                Math.Round(Ratio(r.Events, r.Visitors), 2)))
            .ToList();
    }

    public async Task<IReadOnlyList<PropKeyRow>> PropKeysAsync(StatsRequest request, CancellationToken ct)
    {
        var rows = await RunAsync<PropKeySqlRow>(StatsSqlBuilder.PropKeys(request), ct);
        return rows.Select(r => new PropKeyRow(r.Key, r.Events)).ToList();
    }

    private static double Ratio(long part, long whole) => whole == 0 ? 0 : (double)part / whole;

    public async Task<IReadOnlyList<TimeseriesPoint>> TimeseriesAsync(
        StatsRequest request, string interval, CancellationToken ct)
    {
        var rows = await RunAsync<TimeseriesRow>(StatsSqlBuilder.Timeseries(request, interval), ct);
        // The bucket is a timestamp without time zone, expressed in the requested time zone.
        return rows.Select(r => new TimeseriesPoint(
                r.Bucket.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                r.Visitors, r.Pageviews, r.Events))
            .ToList();
    }

    private async Task<List<T>> RunAsync<T>(BuiltQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(QueryTimeout);

        try
        {
            return await db.Database
                .SqlQueryRaw<T>(ToEfPlaceholders(query.Sql), query.Params.Select(v => v ?? DBNull.Value).ToArray())
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            // e.g. a time zone name that .NET knows but Postgres does not: the caller's mistake, not a server error.
            throw new ArgumentException(ex.MessageText, ex);
        }
    }

    /// <summary>
    /// EF Core names every raw-SQL parameter itself (@p0, @p1, ...) and substitutes them for {0}, {1}, ... with
    /// string.Format, so the builder's $1, $2, ... become {0}, {1}, .... The builder never emits braces, but a
    /// literal one would break string.Format, hence the guard.
    /// </summary>
    internal static string ToEfPlaceholders(string sql)
    {
        if (sql.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new InvalidOperationException("Stats SQL must not contain literal braces.");
        return Placeholder().Replace(sql, m => "{" + (int.Parse(m.Groups[1].ValueSpan) - 1) + "}");
    }

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex Placeholder();

    // Result shapes; column names match the aliases in StatsSqlBuilder.

    private sealed class SummaryRow
    {
        [Column("visitors")] public long Visitors { get; init; }
        [Column("pageviews")] public long Pageviews { get; init; }
        [Column("events")] public long Events { get; init; }
        [Column("visits")] public long Visits { get; init; }
        [Column("bounce_rate")] public double BounceRate { get; init; }
        [Column("avg_duration")] public double AvgDuration { get; init; }
    }

    private sealed class BreakdownSqlRow
    {
        [Column("dim")] public string? Dim { get; init; }
        [Column("visitors")] public long Visitors { get; init; }
        [Column("pageviews")] public long Pageviews { get; init; }
        [Column("events")] public long Events { get; init; }
        [Column("total_visitors")] public long? TotalVisitors { get; init; }
    }

    private sealed class SessionBreakdownSqlRow
    {
        [Column("dim")] public string? Dim { get; init; }
        [Column("visitors")] public long Visitors { get; init; }
        [Column("visits")] public long Visits { get; init; }
        [Column("pageviews")] public long Pageviews { get; init; }
        [Column("bounce_rate")] public double BounceRate { get; init; }
        [Column("avg_duration")] public double AvgDuration { get; init; }
    }

    private sealed class EventsSqlRow
    {
        [Column("name")] public string Name { get; init; } = "";
        [Column("visitors")] public long Visitors { get; init; }
        [Column("events")] public long Events { get; init; }
        [Column("visits")] public long Visits { get; init; }
        [Column("total_visitors")] public long TotalVisitors { get; init; }
    }

    private sealed class PropKeySqlRow
    {
        [Column("key")] public string Key { get; init; } = "";
        [Column("events")] public long Events { get; init; }
    }

    private sealed class TimeseriesRow
    {
        [Column("bucket")] public DateTime Bucket { get; init; }
        [Column("visitors")] public long Visitors { get; init; }
        [Column("pageviews")] public long Pageviews { get; init; }
        [Column("events")] public long Events { get; init; }
    }
}
