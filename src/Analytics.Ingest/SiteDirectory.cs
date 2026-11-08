using Analytics.Core;
using Microsoft.Extensions.Options;

namespace Analytics.Ingest;

/// <summary>
/// In-memory copy of the registered sites, refreshed periodically. The ingest service never writes to the
/// sites table, so a newly added site becomes live within one refresh interval.
/// </summary>
public sealed class SiteDirectory(ISiteLoader loader, IOptions<IngestOptions> options, ILogger<SiteDirectory> log)
    : BackgroundService
{
    private volatile IReadOnlyDictionary<string, SiteInfo> _sites = new Dictionary<string, SiteInfo>();

    public int Count => _sites.Count;

    public bool TryGet(string domain, out SiteInfo site) => _sites.TryGetValue(domain, out site!);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken); // have sites available before the first request
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SiteRefreshSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var all = await loader.LoadAllAsync(ct);
            _sites = all.ToDictionary(s => s.Domain, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not refresh the site list; continuing with the previous one ({Count} sites)", _sites.Count);
        }
    }
}
