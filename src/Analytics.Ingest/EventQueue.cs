using System.Threading.Channels;
using Analytics.Core;
using Microsoft.Extensions.Options;

namespace Analytics.Ingest;

/// <summary>
/// Bounded in-memory queue with a single background writer that stores events in batches. Request handlers only
/// enqueue and return, so a slow database never slows down visitors' pages.
/// </summary>
public sealed class EventQueue : BackgroundService
{
    // Backoff while the database is unavailable. Events are only lost if the queue fills up (then visitors get 503)
    // or the process stops: at 1-5k events/day the default capacity holds days of traffic.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    /// <summary>Attempts made for a batch while shutting down, before giving up on it.</summary>
    private const int ShutdownAttempts = 3;

    private readonly Channel<EventRecord> _channel;
    private readonly IEventStore _store;
    private readonly IngestOptions _options;
    private readonly ILogger<EventQueue> _log;

    public EventQueue(IEventStore store, IOptions<IngestOptions> options, ILogger<EventQueue> log)
    {
        _store = store;
        _options = options.Value;
        _log = log;
        _channel = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Pending => _channel.Reader.Count;

    private long _lastSuccessTicks = DateTime.UtcNow.Ticks;
    private volatile bool _failing;

    /// <summary>True while the current batch cannot be written (the database is unreachable or rejects it).</summary>
    public bool IsFailing => _failing;

    /// <summary>Time of the last successful write, or of startup.</summary>
    public DateTime LastSuccessUtc => new(Interlocked.Read(ref _lastSuccessTicks), DateTimeKind.Utc);

    /// <summary>Returns false if the queue is full (the caller should answer 503).</summary>
    public bool TryEnqueue(EventRecord record) => _channel.Writer.TryWrite(record);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var maxBatch = Math.Max(1, _options.MaxBatchSize);
        var flushInterval = TimeSpan.FromMilliseconds(Math.Max(10, _options.FlushIntervalMs));
        var batch = new List<EventRecord>(maxBatch);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                Drain(reader, batch, maxBatch);

                // Give a burst a short moment to fill the batch before writing.
                if (batch.Count < maxBatch)
                {
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    window.CancelAfter(flushInterval);
                    try
                    {
                        while (batch.Count < maxBatch && await reader.WaitToReadAsync(window.Token))
                        {
                            Drain(reader, batch, maxBatch);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // flush interval elapsed
                    }
                }

                await FlushAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        // Shutdown: write whatever is still buffered.
        _channel.Writer.TryComplete();
        Drain(reader, batch, int.MaxValue);
        if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None, ShutdownAttempts);
    }

    private static void Drain(ChannelReader<EventRecord> reader, List<EventRecord> batch, int max)
    {
        while (batch.Count < max && reader.TryRead(out var item)) batch.Add(item);
    }

    /// <summary>
    /// Writes the batch, retrying with backoff. While running it never gives up (cancellation ends it); at shutdown
    /// <paramref name="maxAttempts"/> bounds the attempts so the process can stop.
    /// </summary>
    private async Task FlushAsync(List<EventRecord> batch, CancellationToken ct, int maxAttempts = int.MaxValue)
    {
        if (batch.Count == 0) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _store.WriteBatchAsync(batch, ct);
                if (_failing) _log.LogInformation("Writing events works again after {Attempts} attempts", attempt + 1);
                _failing = false;
                Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failing = true;
                if (attempt + 1 >= maxAttempts)
                {
                    _log.LogError(ex, "Dropping {Count} events at shutdown after {Attempts} failed attempts",
                        batch.Count, attempt + 1);
                    return;
                }

                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                // First failure with the full exception, then a short line per retry to keep the log readable.
                if (attempt == 0) _log.LogError(ex, "Writing {Count} events failed; retrying until it succeeds", batch.Count);
                else _log.LogWarning("Writing {Count} events failed (attempt {Attempt}, {Queued} queued): {Error}",
                    batch.Count, attempt + 1, _channel.Reader.Count, ex.Message);

                await Task.Delay(delay, ct); // cancellation ends the loop; the shutdown flush takes over
            }
        }
    }
}
