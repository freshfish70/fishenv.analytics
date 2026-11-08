using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Analytics.Core;
using Analytics.Ingest;
using Analytics.Postgres;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var options = config.GetSection("Ingest").Get<IngestOptions>() ?? new IngestOptions();
builder.Services.Configure<IngestOptions>(config.GetSection("Ingest"));

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 8 * 1024; // events are tiny; anything bigger is not an event
});

builder.Services.AddPostgresStorage(
    config.GetConnectionString("Ingest")
    ?? throw new InvalidOperationException("ConnectionStrings:Ingest is not configured"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VisitorHasher>();
builder.Services.AddSingleton(new UserIdHasher(options.IdentitySecret));

builder.Services.AddSingleton<SiteDirectory>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SiteDirectory>());

builder.Services.AddSingleton<EventQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventQueue>());

builder.Services.AddHttpClient("geoip", c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("analytics-geoip-sync/1.0");
});
// A relative DataDir resolves against the working directory; the resolved path is logged at startup.
var geoDir = Path.Combine(Path.GetFullPath(options.DataDir), "geo");
builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new GeoIpUpdater(
        () => factory.CreateClient("geoip"),
        geoDir,
        sp.GetRequiredService<ILogger<GeoIpUpdater>>());
});
builder.Services.AddHostedService<GeoIpHostedService>();

// Forwarded headers are always processed. Loopback is trusted by default (a reverse proxy on the same host);
// TrustedProxies adds addresses or CIDR networks, e.g. a Docker network such as 172.18.0.0/16.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var entry in options.TrustedProxies)
    {
        if (entry.Contains('/')) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
        else o.KnownProxies.Add(IPAddress.Parse(entry));
    }
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            // IPv6 clients usually control a whole /64, so that is the unit that gets limited.
            ctx.Connection.RemoteIpAddress is { } ip ? VisitorHasher.NormalizeIp(ip) : "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.RateLimitPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.Logger.LogInformation("GeoIP data directory: {Dir}", geoDir);

app.UseForwardedHeaders();

// The middleware above consumes X-Forwarded-For only from trusted proxies. If one is still present, a proxy is
// in front of us that is not trusted, and every visitor would get the proxy's address. Warn once (without
// logging any address).
var untrustedProxyWarned = 0;
app.Use(async (ctx, next) =>
{
    // When the middleware applied the header it records the original address in X-Original-For (leftover entries
    // of a multi-hop X-Forwarded-For stay in place, which is fine).
    if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") &&
        !ctx.Request.Headers.ContainsKey("X-Original-For") &&
        Interlocked.Exchange(ref untrustedProxyWarned, 1) == 0)
    {
        app.Logger.LogWarning(
            "Received X-Forwarded-For from an address that is not a trusted proxy. If this service runs behind a " +
            "reverse proxy that is not on this host, add its address or network to Ingest:TrustedProxies; " +
            "otherwise all visitors share the proxy's IP");
    }
    await next();
});

// The tracking script posts cross-origin from customers' sites.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/event"))
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            ctx.Response.Headers["Access-Control-Max-Age"] = "86400";
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }
    await next();
});

app.UseRateLimiter();

var trackerJs = ReadEmbedded("tracker.js");

app.MapPost("/api/event", EventEndpoint.HandleAsync);

app.MapGet("/script.js", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Text(trackerJs, "application/javascript; charset=utf-8");
});

// 503 while events cannot be written, so a load balancer or uptime check notices a database problem.
app.MapGet("/healthz", (EventQueue queue, SiteDirectory sites, GeoIpUpdater geo) =>
{
    var failing = queue.IsFailing;
    return Results.Json(
        new
        {
            status = failing ? "failing" : "ok",
            queued = queue.Pending,
            last_write_utc = queue.LastSuccessUtc,
            sites = sites.Count,
            geo_ranges = geo.Current.Count,
        },
        statusCode: failing ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
});

app.Run();

static string ReadEmbedded(string name)
{
    var assembly = Assembly.GetExecutingAssembly();
    var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resource)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
