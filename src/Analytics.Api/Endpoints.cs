using Analytics.Core;

namespace Analytics.Api;

internal sealed record CreateSiteRequest(string? Domain, string? Name, bool? IdentifyUsers);

internal sealed record UpdateSiteRequest(string? Name, bool? IdentifyUsers);

internal static class Endpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Text("ok"));

        var v1 = app.MapGroup("/api/v1");
        v1.AddEndpointFilter(ApiKeyAuth.AuthenticateAsync);

        // Sites
        v1.MapGet("/sites", async (ISiteStore sites, CancellationToken ct) =>
            Results.Ok(new { results = await sites.LoadAllAsync(ct) }));
        v1.MapGet("/sites/{id:long}", async (long id, ISiteStore sites, CancellationToken ct) =>
            await sites.GetAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound(new { error = "unknown site" }));
        v1.MapPost("/sites", CreateSite).RequireAdmin();
        v1.MapPatch("/sites/{id:long}", UpdateSite).RequireAdmin();
        v1.MapDelete("/sites/{id:long}", DeleteSite).RequireAdmin();

        // API keys (create them with the create-key command; the key is shown only then)
        v1.MapGet("/keys", async (IApiKeyStore keys, CancellationToken ct) =>
            Results.Ok(new { results = await keys.ListAsync(ct) })).RequireAdmin();
        v1.MapDelete("/keys/{id:long}", async (long id, IApiKeyStore keys, CancellationToken ct) =>
        {
            if (!await keys.DeleteAsync(id, ct)) return Results.NotFound(new { error = "unknown key" });
            ApiKeyAuth.MarkRevoked(id);
            return Results.NoContent();
        }).RequireAdmin();

        // Statistics
        var site = v1.MapGroup("/sites/{id:long}");
        site.MapGet("/summary", Summary);
        site.MapGet("/breakdown", Breakdown);
        site.MapGet("/timeseries", Timeseries);
        site.MapGet("/events", Events);
        site.MapGet("/events/props", EventProps);
    }

    private static async Task<IResult> CreateSite(CreateSiteRequest body, ISiteStore sites, CancellationToken ct)
    {
        var domain = SiteDomain.Normalize(body.Domain);
        if (domain is null) return Results.BadRequest(new { error = "invalid domain" });

        var site = await sites.CreateAsync(domain, TextSanitizer.Clean(body.Name, 100), body.IdentifyUsers ?? false, ct);
        return site is null
            ? Results.Conflict(new { error = "a site with this domain already exists" })
            : Results.Created($"/api/v1/sites/{site.Id}", site);
    }

    private static async Task<IResult> UpdateSite(long id, UpdateSiteRequest body, ISiteStore sites, CancellationToken ct) =>
        await sites.UpdateAsync(id, TextSanitizer.Clean(body.Name, 100), body.IdentifyUsers, ct) is { } site
            ? Results.Ok(site)
            : Results.NotFound(new { error = "unknown site" });

    private static async Task<IResult> DeleteSite(long id, ISiteStore sites, CancellationToken ct) =>
        await sites.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "unknown site" });

    private static async Task<IResult> Summary(
        long id, HttpRequest req, ISiteStore sites, IStatsQuery stats, TimeProvider time, CancellationToken ct)
    {
        if (await sites.GetAsync(id, ct) is null) return Results.NotFound(new { error = "unknown site" });
        if (!TryBuildQuery(req, id, time, out var q, out var error)) return Results.BadRequest(new { error });

        return await BadRequestOnArgument(async () =>
        {
            var current = stats.SummaryAsync(q!.Request, ct);
            if (q.Compare is null) return Results.Ok(new { period = Period(q.Request), results = await current });

            var previous = stats.SummaryAsync(q.PreviousTotals, ct);
            await Task.WhenAll(current, previous);
            return Results.Ok(new
            {
                period = Period(q.Request),
                results = current.Result,
                comparison = new
                {
                    period = Period(q.PreviousTotals),
                    results = previous.Result,
                    change = SummaryChange.Between(current.Result, previous.Result),
                },
            });
        });
    }

    private static async Task<IResult> Breakdown(
        long id, HttpRequest req, ISiteStore sites, IStatsQuery stats, TimeProvider time, CancellationToken ct)
    {
        if (await sites.GetAsync(id, ct) is null) return Results.NotFound(new { error = "unknown site" });
        if (!TryBuildQuery(req, id, time, out var q, out var error)) return Results.BadRequest(new { error });

        var by = req.Query["by"].ToString();
        if (!by.StartsWith(StatsDimensions.PropPrefix, StringComparison.OrdinalIgnoreCase)) by = by.ToLowerInvariant();
        else by = StatsDimensions.PropPrefix + by[StatsDimensions.PropPrefix.Length..];

        if (!StatsDimensions.IsValid(by))
        {
            return Results.BadRequest(new
            {
                error = $"'by' must be one of: {string.Join(", ", StatsDimensions.Names)}, or prop.<key>",
            });
        }

        var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Clamp(l, 1, 100) : 10;

        return await BadRequestOnArgument(() => StatsDimensions.IsSessionDimension(by)
            ? Rows(q!, (r, n) => stats.SessionBreakdownAsync(r, by, n, ct), limit,
                row => row.Value, v => new SessionBreakdownRow(v, 0, 0, 0, 0, 0))
            : Rows(q!, (r, n) => stats.BreakdownAsync(r, by, n, ct), limit,
                row => row.Value, v => new BreakdownRow(v, 0, 0, 0)));
    }

    private static async Task<IResult> Events(
        long id, HttpRequest req, ISiteStore sites, IStatsQuery stats, TimeProvider time, CancellationToken ct)
    {
        if (await sites.GetAsync(id, ct) is null) return Results.NotFound(new { error = "unknown site" });
        if (!TryBuildQuery(req, id, time, out var q, out var error)) return Results.BadRequest(new { error });

        var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Clamp(l, 1, StatsSqlBuilder.MaxLimit) : 100;
        return await BadRequestOnArgument(() =>
            Rows(q!, (r, n) => stats.EventsAsync(r, n, ct), limit,
                row => row.Name, v => new EventStatsRow(v ?? "", 0, 0, 0, 0, 0)));
    }

    private static async Task<IResult> EventProps(
        long id, HttpRequest req, ISiteStore sites, IStatsQuery stats, TimeProvider time, CancellationToken ct)
    {
        if (await sites.GetAsync(id, ct) is null) return Results.NotFound(new { error = "unknown site" });
        if (!TryBuildQuery(req, id, time, out var q, out var error)) return Results.BadRequest(new { error });

        return await BadRequestOnArgument(async () =>
            Results.Ok(new { period = Period(q!.Request), results = await stats.PropKeysAsync(q.Request, ct) }));
    }

    private static async Task<IResult> Timeseries(
        long id, HttpRequest req, ISiteStore sites, IStatsQuery stats, TimeProvider time, CancellationToken ct)
    {
        if (await sites.GetAsync(id, ct) is null) return Results.NotFound(new { error = "unknown site" });
        if (!TryBuildQuery(req, id, time, out var q, out var error)) return Results.BadRequest(new { error });

        var interval = req.Query["interval"].ToString().ToLowerInvariant();
        if (interval.Length == 0) interval = "day";
        if (!StatsSqlBuilder.Intervals.Contains(interval))
        {
            return Results.BadRequest(new { error = "'interval' must be one of: hour, day, week, month" });
        }

        return await BadRequestOnArgument(async () =>
        {
            var current = stats.TimeseriesAsync(q!.Request, interval, ct);
            if (q.Compare is null)
                return Results.Ok(new { period = Period(q.Request), interval, results = await current });

            // The whole comparison range, also when the current one has not ended: the chart shows both in full.
            var previous = stats.TimeseriesAsync(q.PreviousSeries, interval, ct);
            await Task.WhenAll(current, previous);
            return Results.Ok(new
            {
                period = Period(q.Request),
                interval,
                results = current.Result,
                comparison = new { period = Period(q.PreviousSeries), results = previous.Result },
            });
        });
    }

    /// <summary>
    /// A list of rows, plus with ?compare= the previous period's rows for the same values, in the same order
    /// (values that did not occur then are zero).
    /// </summary>
    private static async Task<IResult> Rows<T>(
        StatsQuery q, Func<StatsRequest, int, Task<IReadOnlyList<T>>> run, int limit,
        Func<T, string?> key, Func<string?, T> missing)
    {
        var current = run(q.Request, limit);
        if (q.Compare is null) return Results.Ok(new { period = Period(q.Request), results = await current });

        // Look further down the previous period's list so that the current top values are found there.
        var previous = run(q.PreviousTotals, StatsSqlBuilder.MaxLimit);
        await Task.WhenAll(current, previous);
        return Results.Ok(new
        {
            period = Period(q.Request),
            results = current.Result,
            comparison = new
            {
                period = Period(q.PreviousTotals),
                results = StatsComparison.Align(current.Result, previous.Result, key, missing),
            },
        });
    }

    /// <summary>The stats layer reports invalid input it can only detect late (e.g. a time zone Postgres doesn't know)
    /// as <see cref="ArgumentException"/>.</summary>
    private static async Task<IResult> BadRequestOnArgument(Func<Task<IResult>> run)
    {
        try
        {
            return await run();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static object Period(StatsRequest r) => new { from = r.FromUtc, to = r.ToUtc, tz = r.TimeZone };

    private sealed record StatsQuery(StatsRequest Request, ComparisonRange? Compare)
    {
        /// <summary>The comparison range for totals (summary, breakdown, events).</summary>
        public StatsRequest PreviousTotals => Request with { FromUtc = Compare!.FromUtc, ToUtc = Compare.AggregateToUtc };

        /// <summary>The whole comparison range (timeseries).</summary>
        public StatsRequest PreviousSeries => Request with { FromUtc = Compare!.FromUtc, ToUtc = Compare.ToUtc };
    }

    /// <summary>Reads period/from/to/tz, compare/compare_from/compare_to and the filters from the query string.</summary>
    private static bool TryBuildQuery(HttpRequest req, long siteId, TimeProvider time, out StatsQuery? result, out string? error)
    {
        result = null;

        var tzName = req.Query["tz"].ToString();
        if (tzName.Length == 0) tzName = "UTC";

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"unknown time zone '{tzName}' (use an IANA name such as Europe/Oslo)";
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        string? period = req.Query["period"], from = req.Query["from"], to = req.Query["to"];
        if (!DateRangeResolver.TryResolve(period, from, to, tz, now, out var fromUtc, out var toUtc, out error))
        {
            return false;
        }

        var customRange = !string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to);
        if (!DateRangeResolver.TryResolveComparison(
                req.Query["compare"], req.Query["compare_from"], req.Query["compare_to"],
                period, customRange, tz, now, fromUtc, toUtc, out var compare, out error))
        {
            return false;
        }

        var pairs = req.Query.SelectMany(kv => kv.Value.Select(v => KeyValuePair.Create(kv.Key, v ?? "")));
        if (!StatsFilterParser.TryParse(pairs, out var filters, out error)) return false;

        result = new StatsQuery(new StatsRequest(siteId, fromUtc, toUtc, filters, tzName), compare);
        return true;
    }
}
