using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Analytics.Postgres;

/// <summary>
/// Lets the dotnet-ef tool build the model without starting a service:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Analytics.Postgres</c>.
/// Generating a migration does not connect; commands that do (e.g. <c>database update</c>) use
/// the <c>ConnectionStrings__Api</c> environment variable.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Api")
                               ?? "Host=localhost;Database=analytics;Username=analytics_admin";
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>().UseNpgsql(connectionString).Options;
        return new AnalyticsDbContext(options);
    }
}
