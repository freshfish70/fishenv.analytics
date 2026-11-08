using Analytics.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Analytics.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres implementations of the storage interfaces. The ingest and API services pass
    /// different connection strings so that each can run with its own, least-privileged database role.
    /// </summary>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services, string connectionString)
    {
        // One data source (connection pool) per service, shared by EF Core and the binary COPY in PgEventStore.
        // Registered through a factory so the container disposes it on shutdown.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        // The stores are singletons, so they take a factory and create a short-lived context per operation.
        services.AddPooledDbContextFactory<AnalyticsDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<IEventStore, PgEventStore>();
        services.AddSingleton<ISiteStore, PgSiteStore>();
        services.AddSingleton<ISiteLoader>(sp => sp.GetRequiredService<ISiteStore>());
        services.AddSingleton<ISaltStore, PgSaltStore>();
        services.AddSingleton<IApiKeyStore, PgApiKeyStore>();
        services.AddSingleton<IStatsQuery, PgStatsQuery>();
        return services;
    }

    /// <summary>
    /// Applies pending EF Core migrations. Only the API service calls this: it connects as the schema owner,
    /// while the ingest role has no DDL rights.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AnalyticsDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
