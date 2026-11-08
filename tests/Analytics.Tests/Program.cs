// Dependency-free test runner for Analytics.Core: `dotnet run --project tests/Analytics.Tests`
// Exit code is 0 when every check passes. Easy to port to xunit/NUnit later (each Section is a test class).

using System.Net;
using System.Text;
using System.Text.Json;
using Analytics.Core;
using Microsoft.Extensions.Logging.Abstractions;

var t = new Runner();

await Section("RIR parsing", () =>
{
    const string sample = """
        2|ripencc|20261107|1234|19830705|20261107|+0100
        # a comment
        ripencc|*|ipv4|*|3|summary
        ripencc|NO|ipv4|192.0.2.0|768|20110101|allocated
        ripencc|NO|ipv6|2001:db8::|32|20080101|allocated
        ripencc|ZZ|ipv4|198.51.100.0|256||reserved
        ripencc||ipv4|198.51.100.0|256||available
        apnic|JP|ipv4|203.0.113.0|256|20110101|assigned
        ripencc|SE|ipv6|2001:db8:1::|128|20200101|allocated
        ripencc|DE|ipv4|10.0.0.0|0|20200101|allocated
        ripencc|XX|ipv4|255.255.255.0|512|20200101|allocated
        ripencc|FR|ipv4|garbage|256|20200101|allocated
        """;

    var ranges = RirParser.Parse(new StringReader(sample)).ToList();
    t.Eq(4, ranges.Count, "keeps only valid allocated/assigned ranges");

    var no4 = ranges.Single(r => r.Country == "NO" && !r.IsIPv6);
    t.Eq((UInt128)IpV4("192.0.2.0"), no4.Start, "IPv4 start");
    t.Eq((UInt128)IpV4("192.0.4.255"), no4.End, "IPv4 end for a 768-address (non power of two) block");

    var se = ranges.Single(r => r.Country == "SE");
    t.Check(se.Start == se.End, "IPv6 /128 is a single address");
    return Task.CompletedTask;
});

await Section("GeoIP lookup", () =>
{
    const string sample = """
        ripencc|NO|ipv4|192.0.2.0|768|20110101|allocated
        ripencc|NO|ipv6|2001:db8::|32|20080101|allocated
        apnic|JP|ipv4|203.0.113.0|256|20110101|assigned
        ripencc|SE|ipv6|2001:db8:1::|128|20200101|allocated
        ripencc|DK|ipv4|10.0.0.0|16777216|20200101|allocated
        ripencc|FI|ipv4|10.1.0.0|256|20200101|allocated
        """;
    var db = GeoIpDatabase.Build(RirParser.Parse(new StringReader(sample)));

    t.Eq("NO", db.Lookup(IPAddress.Parse("192.0.2.0")), "first address of block");
    t.Eq("NO", db.Lookup(IPAddress.Parse("192.0.4.255")), "last address of block");
    t.Eq(null, db.Lookup(IPAddress.Parse("192.0.5.0")), "address just after block");
    t.Eq(null, db.Lookup(IPAddress.Parse("192.0.1.255")), "address just before block");
    t.Eq("JP", db.Lookup(IPAddress.Parse("203.0.113.77")), "second block");
    t.Eq("JP", db.Lookup(IPAddress.Parse("::ffff:203.0.113.5")), "IPv4-mapped IPv6 address");
    t.Eq("NO", db.Lookup(IPAddress.Parse("2001:db8:ffff::1")), "IPv6 inside /32");
    t.Eq(null, db.Lookup(IPAddress.Parse("2001:db9::1")), "IPv6 outside /32");
    t.Eq("SE", db.Lookup(IPAddress.Parse("2001:db8:1::")), "nested /128 wins over enclosing /32");
    t.Eq("NO", db.Lookup(IPAddress.Parse("2001:db8:1::1")), "next address falls back to the enclosing range");
    t.Eq("DK", db.Lookup(IPAddress.Parse("10.0.0.1")), "outer of a nested IPv4 pair");
    t.Eq("FI", db.Lookup(IPAddress.Parse("10.1.0.200")), "inner of a nested IPv4 pair");
    t.Eq("DK", db.Lookup(IPAddress.Parse("10.1.1.0")), "after the inner range, back in the outer one (backwards walk)");
    t.Eq(null, GeoIpDatabase.Empty.Lookup(IPAddress.Parse("1.1.1.1")), "empty database");
    return Task.CompletedTask;
});

await Section("GeoIP updater", async () =>
{
    var dir = Directory.CreateTempSubdirectory("geo-test").FullName;
    var sources = new List<RirSource> { new("a", "http://test/a"), new("b", "http://test/b") };
    var files = new Dictionary<string, string>
    {
        ["http://test/a"] = "ripencc|NO|ipv4|192.0.2.0|256|20110101|allocated\n",
        ["http://test/b"] = "apnic|JP|ipv4|203.0.113.0|256|20110101|assigned\napnic|SE|ipv4|198.51.100.0|256|20110101|assigned\n",
    };
    var failing = false;
    var requests = 0;

    HttpClient Http() => new(new FakeHandler(req =>
    {
        requests++;
        return failing || !files.TryGetValue(req.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }));

    var updater = new GeoIpUpdater(Http, dir, NullLogger<GeoIpUpdater>.Instance, sources, minRowsPerRegistry: 1);

    t.Eq(2, await updater.SyncAsync(CancellationToken.None), "both registries updated");
    t.Eq("NO", updater.Lookup(IPAddress.Parse("192.0.2.10")), "lookup after sync (registry a)");
    t.Eq("SE", updater.Lookup(IPAddress.Parse("198.51.100.1")), "lookup after sync (registry b)");
    t.Eq(2, Directory.GetFiles(dir, "delegated-*.txt").Length, "cache files written");
    t.Check(!Directory.GetFiles(dir, "*.tmp").Any(), "no temp files left behind");

    requests = 0;
    t.Eq(0, await updater.SyncAsync(TimeSpan.FromHours(24), CancellationToken.None), "fresh files are not downloaded again");
    t.Eq(0, requests, "no request for fresh files");

    failing = true;
    t.Eq(0, await updater.SyncAsync(CancellationToken.None), "failed downloads update nothing");
    t.Eq("NO", updater.Lookup(IPAddress.Parse("192.0.2.10")), "previous data kept after a failed sync");

    var reloaded = new GeoIpUpdater(Http, dir, NullLogger<GeoIpUpdater>.Instance, sources, minRowsPerRegistry: 1);
    await reloaded.LoadCacheAsync(CancellationToken.None);
    t.Eq("JP", reloaded.Lookup(IPAddress.Parse("203.0.113.9")), "a fresh instance loads the on-disk cache");

    // Stale files: each registry is attempted once per period, even if the download keeps failing.
    foreach (var f in Directory.GetFiles(dir, "delegated-*.txt")) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-2));
    requests = 0;
    await reloaded.SyncAsync(TimeSpan.FromHours(24), CancellationToken.None);
    t.Eq(2, requests, "stale registries are attempted");
    requests = 0;
    await reloaded.SyncAsync(TimeSpan.FromHours(24), CancellationToken.None);
    t.Eq(0, requests, "a failed registry is not retried before the period is over");

    failing = false;
    var strictDir = Directory.CreateTempSubdirectory("geo-test2").FullName;
    var strict = new GeoIpUpdater(Http, strictDir, NullLogger<GeoIpUpdater>.Instance, sources, minRowsPerRegistry: 2);
    t.Eq(1, await strict.SyncAsync(TimeSpan.FromHours(24), CancellationToken.None), "a registry with too few rows is refused, the other is accepted");
    t.Eq(null, strict.Lookup(IPAddress.Parse("192.0.2.10")), "refused registry contributes nothing");
    requests = 0;
    await strict.SyncAsync(TimeSpan.FromHours(24), CancellationToken.None);
    t.Eq(0, requests, "a registry without data waits for the retry interval, the fresh one is not fetched");
});

await Section("User agent parsing", () =>
{
    void Check(string name, string ua, string browser, string os, string device, bool bot = false)
    {
        var r = UserAgentParser.Parse(ua);
        t.Eq($"{browser}|{os}|{device}|{bot}", $"{r.Browser}|{r.Os}|{r.Device}|{r.IsBot}", name);
    }

    Check("Chrome on Windows",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Chrome", "Windows", "desktop");
    Check("Edge on Windows",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
        "Edge", "Windows", "desktop");
    Check("Firefox on Linux",
        "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
        "Firefox", "Linux", "desktop");
    Check("Safari on macOS",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Safari", "macOS", "desktop");
    Check("Safari on iPhone",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
        "Safari", "iOS", "mobile");
    Check("Safari on iPad",
        "Mozilla/5.0 (iPad; CPU OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
        "Safari", "iOS", "tablet");
    Check("Chrome on Android phone",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36",
        "Chrome", "Android", "mobile");
    Check("Chrome on Android tablet",
        "Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Chrome", "Android", "tablet");
    Check("Googlebot", "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Other", "Other", "desktop", bot: true);
    Check("curl", "curl/8.4.0", "Other", "Other", "desktop", bot: true);
    t.Check(UserAgentParser.Parse("").IsBot, "empty user agent counts as a bot");
    t.Check(UserAgentParser.Parse(null).IsBot, "missing user agent counts as a bot");
    return Task.CompletedTask;
});

await Section("Text sanitizing and site domains", () =>
{
    t.Eq("abc", TextSanitizer.Clean("a\u0000b\ud800c", 50), "NUL and lone surrogates removed");
    t.Eq("ab", TextSanitizer.Clean("ab\U0001F600", 3), "truncation never splits a surrogate pair");
    t.Eq("ab\U0001F600", TextSanitizer.Clean("ab\U0001F600", 4), "valid surrogate pair kept");
    t.Eq(null, TextSanitizer.Clean("   ", 10), "blank becomes null");

    t.Eq("example.com", SiteDomain.Normalize("https://WWW.Example.com/foo?x=1"), "URL form");
    t.Eq("example.com", SiteDomain.Normalize("www.example.com:8080"), "host with port");
    t.Eq("localhost", SiteDomain.Normalize("localhost"), "localhost");
    t.Eq(null, SiteDomain.Normalize("not a domain"), "spaces rejected");
    t.Eq(null, SiteDomain.Normalize(""), "empty rejected");
    t.Check(SiteDomain.HostMatchesSite("blog.example.com", "example.com"), "subdomain matches");
    t.Check(SiteDomain.HostMatchesSite("www.example.com", "example.com"), "www matches");
    t.Check(!SiteDomain.HostMatchesSite("badexample.com", "example.com"), "suffix without dot does not match");
    t.Check(!SiteDomain.HostMatchesSite("example.com.evil.net", "example.com"), "prefix does not match");

    var k1 = ApiKeys.Generate();
    t.Check(k1.StartsWith("an_") && k1 != ApiKeys.Generate(), "API keys are prefixed and unique");
    t.Eq(32, ApiKeys.Hash(k1).Length, "API key hash is SHA-256");
    return Task.CompletedTask;
});

await Section("Event validation", () =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    EventRequest Req(string body) => JsonSerializer.Deserialize<EventRequest>(body, json)!;

    var ok = EventValidator.TryValidate(Req("""
        {"site":"https://WWW.Example.com","url":"https://example.com/pricing?utm_source=news&utm_medium=email&x=1#frag",
         "referrer":"https://www.google.com/search?q=1"}
        """), out var ev, out _);
    t.Check(ok, "valid pageview accepted");
    t.Eq("example.com|pageview|/pricing|google.com|news|email|", $"{ev!.SiteDomain}|{ev.Type}|{ev.Path}|{ev.Referrer}|{ev.UtmSource}|{ev.UtmMedium}|{ev.UtmCampaign}",
        "fields normalized (query string and fragment dropped from path)");

    EventValidator.TryValidate(Req("""{"site":"example.com","url":"https://example.com/","referrer":"https://example.com/home"}"""), out ev, out _);
    t.Eq(null, ev!.Referrer, "same-site referrer is dropped");
    EventValidator.TryValidate(Req("""{"site":"example.com","url":"https://example.com/","referrer":"https://blog.example.com/"}"""), out ev, out _);
    t.Eq(null, ev!.Referrer, "subdomain referrer is dropped");

    EventValidator.TryValidate(Req("""{"site":"example.com","name":"signup","url":"/docs/a?utm_campaign=spring"}"""), out ev, out _);
    t.Eq("signup|/docs/a|spring", $"{ev!.Type}|{ev.Path}|{ev.UtmCampaign}", "custom event with a relative URL");

    t.Check(!EventValidator.TryValidate(Req("""{"site":"example.com","name":"bad<name>","url":"/"}"""), out _, out _), "invalid event name rejected");
    t.Check(!EventValidator.TryValidate(Req("""{"site":"example.com"}"""), out _, out _), "missing url rejected");
    t.Check(!EventValidator.TryValidate(Req("""{"url":"/"}"""), out _, out _), "missing site rejected");
    t.Check(!EventValidator.TryValidate(Req("""{"site":"example.com","url":"javascript:alert(1)"}"""), out _, out _), "non-http URL rejected");

    EventValidator.TryValidate(Req(
        """{"site":"example.com","name":"x","url":"/","props":{"plan":"pro","n":3,"ok":true,"nested":{"a":1},"arr":[1],"bad key":"v","nul":"a\u0000b","nothing":null}}"""),
        out ev, out _);
    using (var props = JsonDocument.Parse(ev!.PropsJson!))
    {
        var keys = props.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        t.Eq("n,nul,ok,plan", string.Join(',', keys), "only flat, valid props are kept");
        t.Eq("ab", props.RootElement.GetProperty("nul").GetString(), "control characters removed from prop values");
        t.Eq(3, props.RootElement.GetProperty("n").GetInt32(), "numbers kept as numbers");
    }

    var many = string.Join(',', Enumerable.Range(0, 30).Select(i => $"\"k{i}\":\"v\""));
    EventValidator.TryValidate(Req("{\"site\":\"example.com\",\"url\":\"/\",\"props\":{" + many + "}}"), out ev, out _);
    using (var props = JsonDocument.Parse(ev!.PropsJson!))
    {
        t.Eq(EventValidator.MaxProps, props.RootElement.EnumerateObject().Count(), "number of props is capped");
    }

    EventValidator.TryValidate(Req("""{"site":"example.com","url":"/","props":"nope"}"""), out ev, out _);
    t.Eq(null, ev!.PropsJson, "non-object props ignored");

    // Numbers Postgres' numeric type cannot hold would fail the whole COPY batch.
    EventValidator.TryValidate(Req(
        """{"site":"example.com","name":"x","url":"/","props":{"huge":1e1000000,"tiny":1e-20000,"big":12345678901234567,"f":2.5,"e":1e300}}"""),
        out ev, out _);
    using (var props = JsonDocument.Parse(ev!.PropsJson!))
    {
        var r = props.RootElement;
        t.Check(!r.TryGetProperty("huge", out _), "overflowing number dropped");
        t.Eq("0", r.GetProperty("tiny").GetRawText(), "underflowing number becomes 0 instead of 20000 decimals");
        t.Eq(12345678901234567L, r.GetProperty("big").GetInt64(), "integers keep full precision");
        t.Eq(2.5, r.GetProperty("f").GetDouble(), "fractions kept");
        t.Eq(1e300, r.GetProperty("e").GetDouble(), "large finite numbers kept");
    }

    EventValidator.TryValidate(Req("""{"site":"example.com","url":"https://example.com/bl%C3%A5b%C3%A6r/a%2Fb/%ZZ/smør"}"""), out ev, out _);
    t.Eq("/blåbær/a%2Fb/%ZZ/smør", ev!.Path, "paths are percent-decoded per segment; encoded slashes and invalid escapes stay");
    EventValidator.TryValidate(Req("""{"site":"example.com","url":"https://example.com/a%00b"}"""), out ev, out _);
    t.Eq("/ab", ev!.Path, "decoded control characters are removed");
    return Task.CompletedTask;
});

await Section("Visitor hashing", async () =>
{
    var hasher = new VisitorHasher(new InMemorySaltStore());
    var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    var ua = "Mozilla/5.0 test";

    async Task<string> H(string site, string ip, string agent, DateTime when) =>
        await hasher.HashAsync(site, IPAddress.Parse(ip), agent, when, CancellationToken.None);

    var a = await H("1", "203.0.113.5", ua, now);
    t.Eq(32, a.Length, "32 hex characters");
    t.Eq(a, await H("1", "203.0.113.5", ua, now.AddHours(3)), "stable within a day");
    t.Check(a != await H("1", "203.0.113.6", ua, now), "different IP, different visitor");
    t.Check(a != await H("1", "203.0.113.5", "other", now), "different user agent, different visitor");
    t.Check(a != await H("2", "203.0.113.5", ua, now), "different site, different visitor");
    t.Eq(a, await H("1", "::ffff:203.0.113.5", ua, now), "IPv4-mapped IPv6 hashes like the IPv4 address");

    var recording = new RecordingSaltStore();
    var pruning = new VisitorHasher(recording);
    await pruning.HashAsync("1", IPAddress.Loopback, ua, now, CancellationToken.None);
    await pruning.HashAsync("1", IPAddress.Loopback, ua, now.AddDays(1), CancellationToken.None);
    t.Eq(DateOnly.FromDateTime(now.AddDays(1)), recording.LastPruneBefore, "on a new day every earlier salt is pruned");

    var v6a = await H("1", "2001:db8:aaaa:bbbb:1:2:3:4", ua, now);
    t.Eq(v6a, await H("1", "2001:db8:aaaa:bbbb:ffff:eeee:dddd:cccc", ua, now), "IPv6 addresses in the same /64 are one visitor");
    t.Check(v6a != await H("1", "2001:db8:aaaa:cccc::1", ua, now), "different /64, different visitor");
    // Last: moving to the next day prunes today's salt.
    t.Check(a != await H("1", "203.0.113.5", ua, now.AddDays(1)), "visitor changes the next day (salt rotation)");
});

await Section("Date ranges", () =>
{
    var tz = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Oslo", out var oslo) ? oslo : TimeZoneInfo.Utc;
    var oslotz = tz.Id == "UTC";
    var now = new DateTime(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc); // 01:30 in Oslo (CET, UTC+1)
    string F(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm'Z'");

    bool R(string? p, string? f, string? to, out DateTime a, out DateTime b, out string? e) =>
        DateRangeResolver.TryResolve(p, f, to, tz, now, out a, out b, out e);

    if (oslotz) Console.WriteLine("  (tzdata for Europe/Oslo not found; running date checks in UTC)");
    var off = oslotz ? 0 : 1;

    R("today", null, null, out var from, out var to, out _);
    t.Eq($"{F(new DateTime(2026, 11, 7, 0, 0, 0).AddHours(-off))}..{F(new DateTime(2026, 11, 8, 0, 0, 0).AddHours(-off))}", $"{F(from)}..{F(to)}", "today in the local time zone");

    R("7d", null, null, out from, out to, out _);
    t.Eq($"{F(new DateTime(2026, 11, 1, 0, 0, 0).AddHours(-off))}..{F(new DateTime(2026, 11, 8, 0, 0, 0).AddHours(-off))}", $"{F(from)}..{F(to)}", "last 7 days including today");

    R(null, null, null, out from, out to, out _);
    t.Eq(30.0, Math.Round((to - from).TotalDays), "default is 30 days");

    R("month", null, null, out from, out to, out _);
    t.Eq("2026-11", from.AddHours(off).ToString("yyyy-MM"), "current month starts on the 1st");

    R(null, "2026-10-01", "2026-10-31", out from, out to, out _);
    if (!oslotz)
    {
        t.Eq("2026-09-30T22:00Z..2026-10-31T23:00Z", $"{F(from)}..{F(to)}", "date bounds follow DST (CEST at start, CET at end)");
    }

    t.Check(!R("bogus", null, null, out _, out _, out _), "unknown period rejected");
    t.Check(!R(null, "2026-10-31", "2026-10-01", out _, out _, out _), "reversed range rejected");
    t.Check(!R(null, "2020-01-01", "2026-01-01", out _, out _, out _), "range over the limit rejected");
    t.Check(!R(null, null, "2026-10-01", out _, out _, out _), "'to' without 'from' rejected");
    t.Check(R(null, "2026-11-01T00:00:00Z", "2026-11-02T00:00:00Z", out from, out to, out _) && (to - from).TotalHours == 24, "ISO timestamps accepted as-is");
    return Task.CompletedTask;
});

await Section("Stats filters and SQL builder", () =>
{
    var pairs = new[]
    {
        KeyValuePair.Create("country", "no"), KeyValuePair.Create("prop.Plan", "pro"), KeyValuePair.Create("from", "x"),
        KeyValuePair.Create("path", "/a"), KeyValuePair.Create("referrer", "(none)"), KeyValuePair.Create("utm_source", ""),
    };
    t.Check(StatsFilterParser.TryParse(pairs, out var filters, out _), "filters parsed");
    t.Eq("country=NO;prop.Plan=pro;path=/a;referrer=<null>",
        string.Join(';', filters.Select(f => $"{f.Dimension}={f.Value ?? "<null>"}")),
        "known keys kept, values normalized, blanks and unknown keys ignored");
    t.Check(!StatsFilterParser.TryParse([KeyValuePair.Create("prop.bad key", "x")], out _, out _), "invalid property key rejected");

    var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    var to = from.AddDays(7);
    var request = new StatsRequest(42, from, to, filters, "Europe/Oslo");

    var q = StatsSqlBuilder.Summary(request);
    t.Eq(7, q.Params.Count, "summary: site, from, to + 4 filter params");
    t.Check(
        q.Sql.Contains("country = $4::text") &&
        q.Sql.Contains("(props ->> $5::text) = $6::text") &&
        q.Sql.Contains("path = $7::text"),
        "summary: filters are positional parameters");
    t.Check(q.Sql.Contains("referrer IS NULL"), "(none) becomes IS NULL");
    t.Eq(42L, q.Params[0], "site id is parameter 1");

    var evil = new StatsRequest(1, from, to, [new StatsFilter("path", "'; DROP TABLE events; --")]);
    var q2 = StatsSqlBuilder.Summary(evil);
    t.Check(!q2.Sql.Contains("DROP TABLE") && q2.Params.Contains("'; DROP TABLE events; --"), "values never reach the SQL text");

    var b = StatsSqlBuilder.Breakdown(request, "prop.plan", 5000);
    t.Check(b.Sql.Contains("LIMIT $") && (int)b.Params[^1]! == 1000, "breakdown limit is clamped");
    t.Check(b.Params.Contains("plan"), "breakdown by property passes the key as a parameter");
    t.Check(Throws<ArgumentException>(() => StatsSqlBuilder.Breakdown(request, "x; DROP TABLE events", 10)), "unknown dimension rejected");

    var ts = StatsSqlBuilder.Timeseries(request, "day");
    t.Check(ts.Params.Contains("Europe/Oslo") && ts.Sql.Contains("date_trunc('day'"), "timeseries uses the requested time zone and interval");
    t.Check(Throws<ArgumentException>(() => StatsSqlBuilder.Timeseries(request, "day'); DROP TABLE events; --")), "unknown interval rejected");

    // Every $n used in the SQL must have a matching parameter, and every parameter must be used.
    foreach (var (name, query) in new[] { ("summary", q), ("breakdown", b), ("timeseries", ts) })
    {
        var used = System.Text.RegularExpressions.Regex.Matches(query.Sql, @"\$(\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
        t.Eq(string.Join(',', Enumerable.Range(1, query.Params.Count)), string.Join(',', used), $"{name}: placeholders match parameters");
    }
    return Task.CompletedTask;
});

await Section("Filter operators", () =>
{
    var pairs = new[]
    {
        KeyValuePair.Create("path[contains]", "50%_off"), KeyValuePair.Create("country[not]", "no"),
        KeyValuePair.Create("country", "SE"), KeyValuePair.Create("country", "DK"),
        KeyValuePair.Create("prop.plan[prefix]", "pro"), KeyValuePair.Create("referrer[not]", "(none)"),
        KeyValuePair.Create("entry_page[NOT_CONTAINS]", "/admin"), KeyValuePair.Create("foo[contains]", "x"),
    };
    t.Check(StatsFilterParser.TryParse(pairs, out var filters, out _), "operator filters parsed");
    t.Eq("path Contains 50%_off;country IsNot NO;country Is SE;country Is DK;prop.plan Prefix pro;referrer IsNot <null>;entry_page NotContains /admin",
        string.Join(';', filters.Select(f => $"{f.Dimension} {f.Op} {f.Value ?? "<null>"}")),
        "bracket operators, case-insensitive operator names, unknown dimensions ignored");
    t.Check(!StatsFilterParser.TryParse([KeyValuePair.Create("path[like]", "x")], out _, out var err) && err!.Contains("operator"), "unknown operator rejected");
    t.Check(!StatsFilterParser.TryParse([KeyValuePair.Create("path[contains]", "(none)")], out _, out _), "(none) with contains rejected");

    var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    var r = new StatsRequest(1, from, from.AddDays(7), filters);
    var q = StatsSqlBuilder.Summary(r);
    t.Check(q.Sql.Contains("path ILIKE $4::text ESCAPE '\\'") && q.Params.Contains("%50\\%\\_off%"), "contains escapes LIKE wildcards");
    t.Check(q.Sql.Contains("country IS DISTINCT FROM $5::text"), "not equal treats unset as different");
    t.Check(q.Sql.Contains("(country = $6::text OR country = $7::text)"), "repeated equality on one dimension is OR");
    t.Check(q.Sql.Contains("LIKE $9::text ESCAPE") && q.Params.Contains("pro%"), "prefix");
    t.Check(q.Sql.Contains("referrer IS NOT NULL"), "not (none) means is set");
    t.Check(q.Sql.Contains("WHERE COALESCE(entry_page, '') NOT ILIKE"), "visit filter applied to visits, not events");
    t.Check(!q.Sql.Contains("ts >= $2::timestamptz AND entry_page"), "visit filter not in the event WHERE");
    t.Check(q.Sql.Contains("AS entry_page"), "summary computes entry pages only when filtering on them");
    t.Check(!StatsSqlBuilder.Summary(new StatsRequest(1, from, from.AddDays(1), [])).Sql.Contains("ARRAY_AGG"), "plain summary has no page arrays");
    t.Eq("a\\%b\\_c\\\\d", StatsSqlBuilder.EscapeLike("a%b_c\\d"), "LIKE escaping");
    return Task.CompletedTask;
});

await Section("Visit dimensions, events and conversion", () =>
{
    var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    var to = from.AddDays(7);
    StatsRequest R(params StatsFilter[] f) => new(1, from, to, f);

    t.Check(StatsDimensions.IsValid("entry_page") && StatsDimensions.IsValid("exit_page") && StatsDimensions.IsValid("identified"), "new dimensions are valid");
    t.Check(Throws<ArgumentException>(() => StatsSqlBuilder.Breakdown(R(), "entry_page", 10)), "breakdown rejects visit dimensions");

    var sb = StatsSqlBuilder.SessionBreakdown(R(new StatsFilter("country", "NO")), "entry_page", 10);
    t.Check(sb.Sql.Contains("SELECT entry_page AS dim") && sb.Sql.Contains("AS bounce_rate") && sb.Sql.Contains("FROM sf"), "entry page breakdown over visits");
    t.Check(sb.Sql.Contains("ORDER BY ts DESC) FILTER (WHERE type = 'pageview'))[1] AS exit_page"), "exit page is the last pageview");
    t.Check(Throws<ArgumentException>(() => StatsSqlBuilder.SessionBreakdown(R(), "path", 10)), "session breakdown rejects event dimensions");

    var plain = StatsSqlBuilder.Breakdown(R(new StatsFilter("country", "NO")), "path", 10);
    t.Check(plain.Sql.Contains("src AS (SELECT * FROM events WHERE") && plain.Sql.Contains("NULL::bigint AS total_visitors"), "plain breakdown: no conversion");

    var bySession = StatsSqlBuilder.Breakdown(R(new StatsFilter("entry_page", "/landing")), "path", 10);
    t.Check(bySession.Sql.Contains("src AS (SELECT n.* FROM n JOIN sf USING (visitor, sess))"), "visit filter narrows event breakdowns to matching visits");

    var goal = StatsSqlBuilder.Breakdown(R(new StatsFilter("event", "buy_click"), new StatsFilter("country", "NO")), "path", 10);
    t.Check(goal.Sql.Contains("COUNT(DISTINCT visitor) FILTER (WHERE type = $5::text)") && goal.Sql.Contains("COUNT(DISTINCT visitor)::bigint AS total_visitors"),
        "event filter becomes the conversion goal");
    t.Check(goal.Sql.Contains("ts < $3::timestamptz AND country = $4::text)"), "goal is not part of the base WHERE");
    var byEvent = StatsSqlBuilder.Breakdown(R(new StatsFilter("event", "buy_click")), "event", 10);
    t.Check(byEvent.Sql.Contains("AND type = $4::text") && byEvent.Sql.Contains("NULL::bigint AS total_visitors"), "by=event keeps the event filter as a plain filter");

    var ev = StatsSqlBuilder.Events(R(new StatsFilter("event", "checkout:", FilterOp.Prefix), new StatsFilter("device", "Mobile")), 50);
    t.Check(ev.Sql.Contains("WHERE type <> 'pageview' AND type LIKE $5::text") && ev.Sql.Contains("AND device = $4::text"), "events: event filter selects names only");
    t.Check(ev.Sql.Contains("COUNT(DISTINCT (visitor, sess))::bigint AS visits"), "events: visits containing the event");

    var keys = StatsSqlBuilder.PropKeys(R(new StatsFilter("event", "modal_open")));
    t.Check(keys.Sql.Contains("jsonb_object_keys") && keys.Params.Contains("modal_open"), "prop keys for an event");

    var ident = StatsSqlBuilder.Breakdown(R(), "identified", 10);
    t.Check(ident.Sql.Contains("left(visitor, 2) = 'u_'"), "identified dimension");

    var all = new (string, BuiltQuery)[]
    {
        ("session breakdown", sb), ("breakdown via visits", bySession), ("goal breakdown", goal), ("events", ev), ("prop keys", keys),
        ("summary with visit filter", StatsSqlBuilder.Summary(R(new StatsFilter("exit_page", "/x"), new StatsFilter("path", "/y")))),
        ("timeseries with visit filter", StatsSqlBuilder.Timeseries(new StatsRequest(1, from, to, [new StatsFilter("entry_page", "/x")], "Europe/Oslo"), "day")),
        ("events with visit filter", StatsSqlBuilder.Events(R(new StatsFilter("entry_page", "/x"), new StatsFilter("event", "a"), new StatsFilter("event", "b")), 10)),
    };
    foreach (var (name, query) in all)
    {
        var used = System.Text.RegularExpressions.Regex.Matches(query.Sql, @"\$(\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
        t.Eq(string.Join(',', Enumerable.Range(1, query.Params.Count)), string.Join(',', used), $"{name}: placeholders match parameters");
        t.Check(query.Sql.IndexOfAny(['{', '}']) < 0, $"{name}: no braces in SQL");
    }
    return Task.CompletedTask;
});

await Section("Comparison periods", () =>
{
    var utc = TimeZoneInfo.Utc;
    var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    string F(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm");
    ComparisonRange? C(string? cmp, string? period, bool custom, DateTime from, DateTime to, string? cf = null, string? ct = null)
    {
        t.Check(DateRangeResolver.TryResolveComparison(cmp, cf, ct, period, custom, utc, now, from, to, out var range, out var e), $"compare {cmp} {period}: {e}");
        return range;
    }

    t.Eq(null, C(null, "7d", false, now, now.AddDays(1)), "no compare, no range");

    var m = C("previous_period", "month", false, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1))!;
    t.Eq("2026-10-01T00:00..2026-11-01T00:00..2026-10-08T12:00", $"{F(m.FromUtc)}..{F(m.ToUtc)}..{F(m.AggregateToUtc)}",
        "month: previous month, totals stop at the same point in time");

    var d7 = C("previous_period", "7d", false, new DateTime(2026, 11, 2), new DateTime(2026, 11, 9))!;
    t.Eq("2026-10-26T00:00..2026-11-02T00:00", $"{F(d7.FromUtc)}..{F(d7.ToUtc)}", "7d: the 7 days before");

    var custom = C("previous_period", null, true, new DateTime(2026, 10, 10), new DateTime(2026, 10, 20))!;
    t.Eq("2026-09-30T00:00..2026-10-10T00:00..2026-10-10T00:00", $"{F(custom.FromUtc)}..{F(custom.ToUtc)}..{F(custom.AggregateToUtc)}",
        "custom range: same length before, finished range not capped");

    var yoy = C("year_over_year", "month", false, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1))!;
    t.Eq("2025-11-01T00:00..2025-12-01T00:00", $"{F(yoy.FromUtc)}..{F(yoy.ToUtc)}", "year over year");

    var cc = C("custom", null, false, new DateTime(2026, 11, 1), new DateTime(2026, 11, 8), "2026-01-01", "2026-01-07")!;
    t.Eq("2026-01-01T00:00..2026-01-08T00:00", $"{F(cc.FromUtc)}..{F(cc.ToUtc)}", "custom comparison range, inclusive end date");

    t.Check(!DateRangeResolver.TryResolveComparison("bogus", null, null, "7d", false, utc, now, now, now.AddDays(1), out _, out _), "unknown compare mode rejected");
    t.Check(!DateRangeResolver.TryResolveComparison("custom", "2026-01-01", null, "7d", false, utc, now, now, now.AddDays(1), out _, out _), "custom without compare_to rejected");

    if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Oslo", out var oslo))
    {
        // 7 local days ending at the start of 2026-11-02 Oslo time; the period before crosses the end of DST (25 Oct).
        var f = new DateTime(2026, 10, 26, 23, 0, 0, DateTimeKind.Utc);
        DateRangeResolver.TryResolveComparison("previous_period", null, null, "7d", false, oslo, now, f, f.AddDays(7), out var r, out _);
        t.Eq("2026-10-19T22:00", F(r!.FromUtc), "previous period keeps local midnight across DST");
    }

    var change = SummaryChange.Between(new SummaryResult(150, 300, 10, 200, 0.40, 60), new SummaryResult(100, 0, 10, 200, 0.50, 30));
    t.Eq("50|<null>|0|0|-10|100", $"{change.Visitors}|{change.Pageviews?.ToString() ?? "<null>"}|{change.Events}|{change.Visits}|{change.BounceRate}|{change.AvgVisitDurationSeconds}",
        "percent changes, null when previous is 0, bounce rate in points");

    var cur = new[] { new BreakdownRow("/a", 5, 5, 0), new BreakdownRow(null, 3, 3, 0), new BreakdownRow("/new", 1, 1, 0) };
    var prev = new[] { new BreakdownRow(null, 2, 2, 0), new BreakdownRow("/b", 9, 9, 0), new BreakdownRow("/a", 4, 4, 0) };
    var aligned = StatsComparison.Align(cur, prev, x => x.Value, v => new BreakdownRow(v, 0, 0, 0));
    t.Eq("/a:4,<null>:2,/new:0", string.Join(',', aligned.Select(x => $"{x.Value ?? "<null>"}:{x.Visitors}")), "previous rows aligned to the current ones");
    return Task.CompletedTask;
});

await Section("Identified users", () =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    EventRequest Req(string body) => JsonSerializer.Deserialize<EventRequest>(body, json)!;

    EventValidator.TryValidate(Req("""{"site":"example.com","url":"/","user":"  42 "}"""), out var ev, out _);
    t.Eq("42", ev!.UserId, "user id trimmed");
    EventValidator.TryValidate(Req("{\"site\":\"example.com\",\"url\":\"/\",\"user\":\"" + new string('x', 300) + "\"}"), out ev, out _);
    t.Eq(null, ev!.UserId, "over-long user id ignored, not truncated");
    EventValidator.TryValidate(Req("""{"site":"example.com","url":"/"}"""), out ev, out _);
    t.Eq(null, ev!.UserId, "no user id");

    var secret = new string('s', 32);
    var h = new UserIdHasher(secret);
    var a = h.Hash(1, "42");
    t.Check(a.StartsWith("u_") && a.Length == 34, "user hash format");
    t.Eq(a, new UserIdHasher(secret).Hash(1, "42"), "stable across instances (and days)");
    t.Check(a != h.Hash(2, "42") && a != h.Hash(1, "43") && a != new UserIdHasher(new string('t', 32)).Hash(1, "42"), "depends on site, user and secret");
    t.Check(!new UserIdHasher(null).IsEnabled && !new UserIdHasher("").IsEnabled, "no secret disables identification");
    t.Check(Throws<ArgumentException>(() => new UserIdHasher("short")), "short secret rejected");
    return Task.CompletedTask;
});

return t.Finish();

// ---------------------------------------------------------------------------------------------

async Task Section(string name, Func<Task> body)
{
    Console.WriteLine($"- {name}");
    try { await body(); }
    catch (Exception ex) { t.Check(false, $"{name}: unexpected exception {ex.GetType().Name}: {ex.Message}"); }
}

static uint IpV4(string s) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(s).GetAddressBytes());

static bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; }
    catch (T) { return true; }
}

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

sealed class RecordingSaltStore : ISaltStore
{
    private readonly InMemorySaltStore _inner = new();
    public DateOnly? LastPruneBefore { get; private set; }

    public Task<byte[]> GetOrCreateAsync(DateOnly day, CancellationToken ct) => _inner.GetOrCreateAsync(day, ct);

    public Task PruneAsync(DateOnly before, CancellationToken ct)
    {
        LastPruneBefore = before;
        return _inner.PruneAsync(before, ct);
    }
}

sealed class Runner
{
    private int _passed, _failed;

    public void Check(bool ok, string name)
    {
        if (ok) { _passed++; return; }
        _failed++;
        Console.WriteLine($"  FAIL: {name}");
    }

    public void Eq<T>(T expected, T actual, string name)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) { _passed++; return; }
        _failed++;
        Console.WriteLine($"  FAIL: {name}\n        expected: {expected}\n        actual:   {actual}");
    }

    public int Finish()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
