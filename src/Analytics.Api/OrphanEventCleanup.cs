using Analytics.Core;

namespace Analytics.Api;

/// <summary>
/// Deletes events of sites that no longer exist, shortly after startup and then daily. After a site is deleted the
/// ingest service keeps accepting its events until its next site refresh (Ingest:SiteRefreshSeconds), and
/// events.site_id has no foreign key, so these few rows would otherwise stay forever. They are invisible in the
/// API (the site is gone), so a daily sweep is soon enough.
/// </summary>
internal sealed class OrphanEventCleanup(ISiteStore sites, ILogger<OrphanEventCleanup> log) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, stoppingToken);
            while (true)
            {
                try
                {
                    var deleted = await sites.DeleteOrphanedEventsAsync(stoppingToken);
                    if (deleted > 0) log.LogInformation("Deleted {Count} events of deleted sites", deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Deleting events of deleted sites failed; retrying in {Hours} h", Interval.TotalHours);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
