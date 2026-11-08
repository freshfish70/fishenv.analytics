using Analytics.Core;
using Microsoft.Extensions.Options;

namespace Analytics.Ingest;

/// <summary>Loads the cached RIR data at startup and refreshes it from the registries when it gets old.</summary>
public sealed class GeoIpHostedService(GeoIpUpdater updater, IOptions<IngestOptions> options, ILogger<GeoIpHostedService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxAge = TimeSpan.FromHours(Math.Max(1, options.Value.GeoRefreshHours));

        try
        {
            await updater.LoadCacheAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Only registries that are due are downloaded (about once per GeoRefreshHours each).
                try
                {
                    await updater.SyncAsync(maxAge, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "GeoIP sync failed");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Countries are optional: never take the ingest service down because of GeoIP problems.
            log.LogError(ex, "GeoIP service stopped unexpectedly; events will be stored without a country");
        }
    }
}
