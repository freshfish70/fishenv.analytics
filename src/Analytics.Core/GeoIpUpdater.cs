using Microsoft.Extensions.Logging;

namespace Analytics.Core;

public sealed record RirSource(string Name, string Url);

/// <summary>
/// Downloads the five RIR delegation files, caches them on disk and keeps an up-to-date in-memory
/// <see cref="GeoIpDatabase"/>. A registry whose download fails (or looks truncated) keeps its previous data.
/// </summary>
public sealed class GeoIpUpdater
{
    public static readonly IReadOnlyList<RirSource> DefaultSources =
    [
        new("arin", "https://ftp.arin.net/pub/stats/arin/delegated-arin-extended-latest"),
        new("ripencc", "https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-extended-latest"),
        new("apnic", "https://ftp.apnic.net/stats/apnic/delegated-apnic-extended-latest"),
        new("lacnic", "https://ftp.lacnic.net/pub/stats/lacnic/delegated-lacnic-extended-latest"),
        new("afrinic", "https://ftp.afrinic.net/pub/stats/afrinic/delegated-afrinic-extended-latest"),
    ];

    private readonly Func<HttpClient> _httpFactory;
    private readonly string _cacheDir;
    private readonly ILogger<GeoIpUpdater> _log;
    private readonly IReadOnlyList<RirSource> _sources;
    private readonly int _minRowsPerRegistry;

    private readonly Dictionary<string, List<GeoRange>> _ranges = new();
    private readonly object _gate = new();
    private GeoIpDatabase _current = GeoIpDatabase.Empty;

    public GeoIpUpdater(
        Func<HttpClient> httpFactory,
        string cacheDir,
        ILogger<GeoIpUpdater> log,
        IReadOnlyList<RirSource>? sources = null,
        int minRowsPerRegistry = 500)
    {
        _httpFactory = httpFactory;
        _cacheDir = cacheDir;
        _log = log;
        _sources = sources ?? DefaultSources;
        _minRowsPerRegistry = minRowsPerRegistry;
    }

    public GeoIpDatabase Current => Volatile.Read(ref _current);

    /// <summary>Country code for the address, or null if unknown or no data has been loaded yet.</summary>
    public string? Lookup(System.Net.IPAddress ip) => Current.Lookup(ip);

    public Task LoadCacheAsync(CancellationToken ct) => Task.Run(() =>
    {
        foreach (var s in _sources)
        {
            ct.ThrowIfCancellationRequested();
            var path = FilePath(s);
            if (!File.Exists(path)) continue;
            try
            {
                var rows = ParseFile(path);
                lock (_gate) _ranges[s.Name] = rows;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not read cached {Registry} data", s.Name);
            }
        }
        Rebuild();
        _log.LogInformation("GeoIP cache loaded: {Count} ranges", Current.Count);
    }, ct);

    /// <summary>How long a registry that has no data at all waits before its download is retried.</summary>
    public static readonly TimeSpan MissingRetryInterval = TimeSpan.FromHours(1);

    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new();

    /// <summary>Downloads every registry file now. Returns the number of registries that were updated.</summary>
    public Task<int> SyncAsync(CancellationToken ct) => SyncAsync(TimeSpan.Zero, ct);

    /// <summary>
    /// Downloads only the registries that are due: a cached file older than <paramref name="maxAge"/> is fetched
    /// again, and each registry is tried at most once per <paramref name="maxAge"/>, so a registry that keeps failing
    /// costs one download attempt per period, not one per check. A registry without any cached file is retried
    /// every <see cref="MissingRetryInterval"/> (or sooner if <paramref name="maxAge"/> is shorter).
    /// Returns the number of registries that were updated.
    /// </summary>
    public async Task<int> SyncAsync(TimeSpan maxAge, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = _sources.Where(s => IsDue(s, maxAge, now)).ToList();
        if (due.Count == 0) return 0;

        Directory.CreateDirectory(_cacheDir);
        var updated = 0;

        using var http = _httpFactory();
        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) _lastAttemptUtc[s.Name] = now;
            var tmp = FilePath(s) + ".tmp";
            try
            {
                using var resp = await http.GetAsync(s.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using (var net = await resp.Content.ReadAsStreamAsync(ct))
                await using (var file = File.Create(tmp))
                {
                    await net.CopyToAsync(file, ct);
                }

                var rows = await Task.Run(() => ParseFile(tmp), ct);
                if (rows.Count < _minRowsPerRegistry)
                {
                    throw new InvalidDataException($"only {rows.Count} usable rows, refusing to replace existing data");
                }

                File.Move(tmp, FilePath(s), overwrite: true);
                lock (_gate) _ranges[s.Name] = rows;
                updated++;
                _log.LogInformation("GeoIP {Registry} updated: {Count} ranges", s.Name, rows.Count);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GeoIP sync of {Registry} failed; keeping previous data", s.Name);
                TryDelete(tmp);
            }
        }

        if (updated > 0) Rebuild();
        return updated;
    }

    private bool IsDue(RirSource s, TimeSpan maxAge, DateTime now)
    {
        DateTime? lastAttempt;
        lock (_gate) lastAttempt = _lastAttemptUtc.TryGetValue(s.Name, out var t) ? t : null;

        var path = FilePath(s);
        if (!File.Exists(path))
        {
            var retry = maxAge < MissingRetryInterval ? maxAge : MissingRetryInterval;
            return lastAttempt is null || now - lastAttempt.Value >= retry;
        }

        return now - File.GetLastWriteTimeUtc(path) >= maxAge &&
               (lastAttempt is null || now - lastAttempt.Value >= maxAge);
    }

    private string FilePath(RirSource s) => Path.Combine(_cacheDir, $"delegated-{s.Name}.txt");

    private static List<GeoRange> ParseFile(string path)
    {
        using var reader = File.OpenText(path);
        return RirParser.Parse(reader).ToList();
    }

    private void Rebuild()
    {
        List<GeoRange> all;
        lock (_gate) all = _ranges.Values.SelectMany(x => x).ToList();
        Volatile.Write(ref _current, GeoIpDatabase.Build(all));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
