using System.Text;

namespace Analytics.Core;

/// <summary>
/// A parameterized SQL statement using positional placeholders ($1, $2, ...). Result columns are aliased;
/// the Postgres project maps rows by those names.
/// </summary>
public sealed record BuiltQuery(string Sql, IReadOnlyList<object?> Params);

/// <summary>
/// Builds the analytics queries. User-supplied values only ever travel as parameters; column names come from a
/// fixed whitelist and the time interval from a fixed set, so no request data is concatenated into the SQL.
///
/// Filters come in two kinds. Event-level filters (path, country, prop.x, ...) select events before visits are
/// built, as they always have. Visit-level filters (entry_page, exit_page) select whole visits afterwards; when
/// one is present, the event-based queries read from the events of the matching visits only.
/// </summary>
public static class StatsSqlBuilder
{
    public static readonly IReadOnlySet<string> Intervals =
        new HashSet<string>(StringComparer.Ordinal) { "hour", "day", "week", "month" };

    public const int MaxLimit = 1000;

    private sealed class ParamList
    {
        public readonly List<object?> Items = [];

        public string Add(object? value)
        {
            Items.Add(value);
            return "$" + Items.Count;
        }
    }

    /// <summary>
    /// Visitors, pageviews, custom events, visits (sessions), bounce rate and average visit duration.
    /// A session is a run of events from the same visitor with gaps of at most 30 minutes.
    /// </summary>
    public static BuiltQuery Summary(StatsRequest r)
    {
        var p = new ParamList();
        var (eventFilters, sessionFilters) = Split(r.Filters);
        var where = BaseWhere(r, p, eventFilters, out _, out _);

        var sql = $"""
            WITH {SessionCtes(where, SessionWhere(sessionFilters, p), "visitor, ts, type, path", sessionFilters.Count > 0)}
            SELECT COUNT(DISTINCT visitor)::bigint AS visitors,
                   COALESCE(SUM(n_pageviews), 0)::bigint AS pageviews,
                   COALESCE(SUM(n_events - n_pageviews), 0)::bigint AS events,
                   COUNT(*)::bigint AS visits,
                   COALESCE(AVG((n_events = 1)::int), 0)::float8 AS bounce_rate,
                   COALESCE(AVG(dur), 0)::float8 AS avg_duration
            FROM sf
            """;

        return new BuiltQuery(sql, p.Items);
    }

    /// <summary>
    /// Top values of one event-level dimension (or custom property) with visitors, pageviews and custom events.
    /// With a filter on <c>event</c> (and <paramref name="by"/> not being <c>event</c> itself) the counts cover the
    /// matching events only, and conversion_rate is the share of the row's visitors (over all their events) who
    /// triggered one.
    /// </summary>
    public static BuiltQuery Breakdown(StatsRequest r, string by, int limit)
    {
        if (StatsDimensions.IsSessionDimension(by))
            throw new ArgumentException($"'{by}' is a visit dimension; use SessionBreakdown", nameof(by));

        var p = new ParamList();
        var goals = by == "event" ? [] : r.Filters.Where(IsGoal).ToList();
        var others = r.Filters.Where(f => !goals.Contains(f)).ToList();

        var source = Source(r, p, others, out _, out _);
        var expr = DimensionExpr(by, p);

        string sql;
        if (goals.Count == 0)
        {
            sql = $"""
                WITH {source}
                SELECT {expr} AS dim,
                       COUNT(DISTINCT visitor)::bigint AS visitors,
                       (COUNT(*) FILTER (WHERE type = 'pageview'))::bigint AS pageviews,
                       (COUNT(*) FILTER (WHERE type <> 'pageview'))::bigint AS events,
                       NULL::bigint AS total_visitors
                FROM src
                GROUP BY 1
                ORDER BY visitors DESC, pageviews DESC, events DESC, dim ASC NULLS LAST
                LIMIT {p.Add(Math.Clamp(limit, 1, MaxLimit))}::int
                """;
        }
        else
        {
            var goal = Conditions(goals, p);
            sql = $"""
                WITH {source}
                SELECT {expr} AS dim,
                       (COUNT(DISTINCT visitor) FILTER (WHERE {goal}))::bigint AS visitors,
                       (COUNT(*) FILTER (WHERE type = 'pageview' AND {goal}))::bigint AS pageviews,
                       (COUNT(*) FILTER (WHERE type <> 'pageview' AND {goal}))::bigint AS events,
                       COUNT(DISTINCT visitor)::bigint AS total_visitors
                FROM src
                GROUP BY 1
                HAVING COUNT(*) FILTER (WHERE {goal}) > 0
                ORDER BY visitors DESC, pageviews DESC, events DESC, dim ASC NULLS LAST
                LIMIT {p.Add(Math.Clamp(limit, 1, MaxLimit))}::int
                """;
        }

        return new BuiltQuery(sql, p.Items);
    }

    /// <summary>
    /// Top values of a visit-level dimension (entry_page, exit_page): visits, visitors, pageviews, bounce rate and
    /// average visit duration per value. The entry page is the first pageview of a visit, the exit page the last.
    /// </summary>
    public static BuiltQuery SessionBreakdown(StatsRequest r, string by, int limit)
    {
        if (!StatsDimensions.TryGetSessionColumn(by, out var column))
            throw new ArgumentException($"unknown visit dimension '{by}'", nameof(by));

        var p = new ParamList();
        var (eventFilters, sessionFilters) = Split(r.Filters);
        var where = BaseWhere(r, p, eventFilters, out _, out _);

        var sql = $"""
            WITH {SessionCtes(where, SessionWhere(sessionFilters, p), "visitor, ts, type, path", withPages: true)}
            SELECT {column} AS dim,
                   COUNT(DISTINCT visitor)::bigint AS visitors,
                   COUNT(*)::bigint AS visits,
                   COALESCE(SUM(n_pageviews), 0)::bigint AS pageviews,
                   COALESCE(AVG((n_events = 1)::int), 0)::float8 AS bounce_rate,
                   COALESCE(AVG(dur), 0)::float8 AS avg_duration
            FROM sf
            GROUP BY 1
            ORDER BY visits DESC, visitors DESC, dim ASC NULLS LAST
            LIMIT {p.Add(Math.Clamp(limit, 1, MaxLimit))}::int
            """;

        return new BuiltQuery(sql, p.Items);
    }

    /// <summary>Visitors, pageviews and custom events per time bucket, with empty buckets filled with zeros.</summary>
    public static BuiltQuery Timeseries(StatsRequest r, string interval)
    {
        if (!Intervals.Contains(interval)) throw new ArgumentException($"unknown interval '{interval}'", nameof(interval));

        var p = new ParamList();
        var source = Source(r, p, r.Filters, out var fromParam, out var toParam);
        var tz = p.Add(r.TimeZone) + "::text";

        var sql = $"""
            WITH {source},
            b AS (
                SELECT generate_series(
                    date_trunc('{interval}', {fromParam}::timestamptz AT TIME ZONE {tz}),
                    date_trunc('{interval}', ({toParam}::timestamptz - interval '1 microsecond') AT TIME ZONE {tz}),
                    interval '1 {interval}') AS bucket
            ),
            d AS (
                SELECT date_trunc('{interval}', ts AT TIME ZONE {tz}) AS bucket,
                       COUNT(DISTINCT visitor)::bigint AS visitors,
                       (COUNT(*) FILTER (WHERE type = 'pageview'))::bigint AS pageviews,
                       (COUNT(*) FILTER (WHERE type <> 'pageview'))::bigint AS events
                FROM src
                GROUP BY 1
            )
            SELECT b.bucket,
                   COALESCE(d.visitors, 0)::bigint AS visitors,
                   COALESCE(d.pageviews, 0)::bigint AS pageviews,
                   COALESCE(d.events, 0)::bigint AS events
            FROM b
            LEFT JOIN d ON d.bucket = b.bucket
            ORDER BY b.bucket
            """;

        return new BuiltQuery(sql, p.Items);
    }

    /// <summary>
    /// Usage per custom event name: visitors, times triggered, visits containing it, and the total number of
    /// visitors matching the other filters (the denominator of the conversion rate). Filters on <c>event</c>
    /// select which event names are listed and do not narrow that denominator.
    /// </summary>
    public static BuiltQuery Events(StatsRequest r, int limit)
    {
        var p = new ParamList();
        var goals = r.Filters.Where(IsGoal).ToList();
        var (eventFilters, sessionFilters) = Split(r.Filters.Where(f => !IsGoal(f)).ToList());
        var where = BaseWhere(r, p, eventFilters, out _, out _);
        var ctes = SessionCtes(where, SessionWhere(sessionFilters, p), "visitor, ts, type, path", sessionFilters.Count > 0);
        var nf = sessionFilters.Count > 0
            ? "SELECT n.visitor, n.sess, n.type FROM n JOIN sf USING (visitor, sess)"
            : "SELECT visitor, sess, type FROM n";
        var goal = goals.Count > 0 ? " AND " + Conditions(goals, p) : "";

        var sql = $"""
            WITH {ctes},
            nf AS ({nf}),
            tot AS (SELECT COUNT(DISTINCT visitor) AS total FROM nf)
            SELECT type AS name,
                   COUNT(DISTINCT visitor)::bigint AS visitors,
                   COUNT(*)::bigint AS events,
                   COUNT(DISTINCT (visitor, sess))::bigint AS visits,
                   (SELECT total FROM tot)::bigint AS total_visitors
            FROM nf
            WHERE type <> 'pageview'{goal}
            GROUP BY type
            ORDER BY visitors DESC, events DESC, name ASC
            LIMIT {p.Add(Math.Clamp(limit, 1, MaxLimit))}::int
            """;

        return new BuiltQuery(sql, p.Items);
    }

    /// <summary>The custom property keys of the matching events with how often each occurs (top 100).</summary>
    public static BuiltQuery PropKeys(StatsRequest r)
    {
        var p = new ParamList();
        var source = Source(r, p, r.Filters, out _, out _);

        var sql = $"""
            WITH {source}
            SELECT k AS key, COUNT(*)::bigint AS events
            FROM (SELECT props FROM src WHERE jsonb_typeof(props) = 'object') x
            CROSS JOIN LATERAL jsonb_object_keys(x.props) AS k
            GROUP BY k
            ORDER BY events DESC, key ASC
            LIMIT 100
            """;

        return new BuiltQuery(sql, p.Items);
    }

    // ---------------------------------------------------------------------------------------------------------

    private static bool IsGoal(StatsFilter f) => f.Dimension == "event";

    private static (List<StatsFilter> EventLevel, List<StatsFilter> SessionLevel) Split(IEnumerable<StatsFilter> filters)
    {
        var all = filters.ToList();
        return (all.Where(f => !StatsDimensions.IsSessionDimension(f.Dimension)).ToList(),
                all.Where(f => StatsDimensions.IsSessionDimension(f.Dimension)).ToList());
    }

    /// <summary>
    /// The CTE <c>src</c>: the events matching all filters, with the columns of <c>events</c>. Without visit-level
    /// filters it is a plain filtered scan (Postgres inlines it); with them, the events of the matching visits.
    /// </summary>
    private static string Source(StatsRequest r, ParamList p, IReadOnlyList<StatsFilter> filters,
        out string fromParam, out string toParam)
    {
        var (eventFilters, sessionFilters) = Split(filters);
        var where = BaseWhere(r, p, eventFilters, out fromParam, out toParam);
        if (sessionFilters.Count == 0) return $"src AS (SELECT * FROM events WHERE {where})";

        var ctes = SessionCtes(where, SessionWhere(sessionFilters, p), "*", withPages: true);
        return $"""
            {ctes},
            src AS (SELECT n.* FROM n JOIN sf USING (visitor, sess))
            """;
    }

    /// <summary>
    /// CTEs that split the selected events into visits: <c>n</c> (the events plus their visit number
    /// <c>sess</c>), <c>s</c> (one row per visit) and <c>sf</c> (the visits passing the visit-level filters).
    /// </summary>
    private static string SessionCtes(string where, string sessionWhere, string columns, bool withPages)
    {
        var pages = withPages
            ? """
              ,
                     (ARRAY_AGG(path ORDER BY ts) FILTER (WHERE type = 'pageview'))[1] AS entry_page,
                     (ARRAY_AGG(path ORDER BY ts DESC) FILTER (WHERE type = 'pageview'))[1] AS exit_page
              """
            : "";

        return $"""
            e AS (
                SELECT {columns} FROM events WHERE {where}
            ),
            g AS (
                SELECT e.*,
                       CASE WHEN LAG(ts) OVER w IS NULL OR ts - LAG(ts) OVER w > interval '30 minutes'
                            THEN 1 ELSE 0 END AS is_new
                FROM e
                WINDOW w AS (PARTITION BY visitor ORDER BY ts)
            ),
            n AS (
                SELECT g.*,
                       SUM(is_new) OVER (PARTITION BY visitor ORDER BY ts ROWS UNBOUNDED PRECEDING) AS sess
                FROM g
            ),
            s AS (
                SELECT visitor, sess,
                       COUNT(*) AS n_events,
                       COUNT(*) FILTER (WHERE type = 'pageview') AS n_pageviews,
                       EXTRACT(EPOCH FROM (MAX(ts) - MIN(ts))) AS dur{pages}
                FROM n
                GROUP BY visitor, sess
            ),
            sf AS (
                SELECT * FROM s WHERE {sessionWhere}
            )
            """;
    }

    private static string SessionWhere(IReadOnlyList<StatsFilter> sessionFilters, ParamList p) =>
        sessionFilters.Count == 0 ? "TRUE" : Conditions(sessionFilters, p);

    /// <summary>Site, time range and the given (event-level) filters. Returns the range-bound placeholders.</summary>
    private static string BaseWhere(StatsRequest r, ParamList p, IReadOnlyList<StatsFilter> filters,
        out string fromParam, out string toParam)
    {
        var sb = new StringBuilder();
        sb.Append("site_id = ").Append(p.Add(r.SiteId));
        fromParam = p.Add(AsUtc(r.FromUtc));
        toParam = p.Add(AsUtc(r.ToUtc));
        sb.Append(" AND ts >= ").Append(fromParam).Append("::timestamptz");
        sb.Append(" AND ts < ").Append(toParam).Append("::timestamptz");
        if (filters.Count > 0) sb.Append(" AND ").Append(Conditions(filters, p));
        return sb.ToString();
    }

    /// <summary>The SQL expression for a dimension: a whitelisted column/expression or a custom property.</summary>
    private static string DimensionExpr(string dimension, ParamList p)
    {
        if (StatsDimensions.TryGetPropKey(dimension, out var key)) return $"(props ->> {p.Add(key)}::text)";
        if (StatsDimensions.TryGetColumn(dimension, out var column)) return column;
        if (StatsDimensions.TryGetSessionColumn(dimension, out var sessionColumn)) return sessionColumn;
        throw new ArgumentException($"unknown dimension '{dimension}'");
    }

    /// <summary>
    /// Filters joined with AND. Positive filters (is, contains, prefix) on the same dimension are ORed, so
    /// <c>country=NO&amp;country=SE</c> means "NO or SE"; negative ones (not, not_contains) all have to hold.
    /// </summary>
    private static string Conditions(IReadOnlyList<StatsFilter> filters, ParamList p)
    {
        var parts = new List<string>();
        foreach (var group in filters.GroupBy(f => f.Dimension))
        {
            var any = new List<string>();
            foreach (var f in group)
            {
                var condition = Condition(DimensionExpr(f.Dimension, p), f, p);
                if (f.Op is FilterOp.IsNot or FilterOp.NotContains) parts.Add(condition);
                else any.Add(condition);
            }
            if (any.Count == 1) parts.Add(any[0]);
            else if (any.Count > 1) parts.Add("(" + string.Join(" OR ", any) + ")");
        }
        return string.Join(" AND ", parts);
    }

    private static string Condition(string expr, StatsFilter f, ParamList p) => (f.Op, f.Value) switch
    {
        (FilterOp.Is, null) => $"{expr} IS NULL",
        (FilterOp.Is, { } v) => $"{expr} = {p.Add(v)}::text",
        (FilterOp.IsNot, null) => $"{expr} IS NOT NULL",
        (FilterOp.IsNot, { } v) => $"{expr} IS DISTINCT FROM {p.Add(v)}::text",
        (FilterOp.Contains, { } v) => $"{expr} ILIKE {p.Add("%" + EscapeLike(v) + "%")}::text ESCAPE '\\'",
        (FilterOp.NotContains, { } v) => $"COALESCE({expr}, '') NOT ILIKE {p.Add("%" + EscapeLike(v) + "%")}::text ESCAPE '\\'",
        (FilterOp.Prefix, { } v) => $"{expr} LIKE {p.Add(EscapeLike(v) + "%")}::text ESCAPE '\\'",
        _ => throw new ArgumentException($"operator {f.Op} needs a value ('{f.Dimension}')"),
    };

    /// <summary>Makes LIKE wildcards in a user value literal (escape character: backslash).</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };
}
