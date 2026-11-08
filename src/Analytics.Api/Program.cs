using System.Text.Json;
using System.Threading.RateLimiting;
using Analytics.Api;
using Analytics.Core;
using Analytics.Postgres;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Api")
                       ?? throw new InvalidOperationException("ConnectionStrings:Api is not configured");

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 16 * 1024;
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddPostgresStorage(connectionString);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();

// Not started for the create-key command, which never runs the host.
builder.Services.AddHostedService<OrphanEventCleanup>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("Api:RateLimitPerMinute", 300),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

if (config.GetValue("Database:AutoMigrate", true))
{
    await app.Services.MigrateDatabaseAsync(CancellationToken.None);
}

// Usage: dotnet Analytics.Api.dll create-key <name> [admin|read]
if (args.Length > 0 && args[0] == "create-key")
{
    var name = args.Length > 1 ? args[1] : "default";
    var scope = args.Length > 2 && args[2].Equals("admin", StringComparison.OrdinalIgnoreCase)
        ? ApiKeyScope.Admin
        : ApiKeyScope.Read;

    var key = ApiKeys.Generate();
    await app.Services.GetRequiredService<IApiKeyStore>()
        .CreateAsync(name, scope, ApiKeys.Hash(key), CancellationToken.None);

    Console.WriteLine($"Created {scope.ToString().ToLowerInvariant()} API key '{name}'. It is shown only once:");
    Console.WriteLine(key);
    return 0;
}

app.UseRateLimiter();
app.MapApi();

await app.RunAsync();
return 0;
