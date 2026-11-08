namespace Analytics.Ingest;

public sealed class IngestOptions
{
    /// <summary>Reject browser requests whose Origin is not the registered site (or one of its subdomains).</summary>
    public bool EnforceOrigin { get; set; } = true;

    /// <summary>
    /// IP addresses or CIDR networks ("172.18.0.0/16") of reverse proxies / load balancers whose X-Forwarded-For
    /// header may be trusted, in addition to loopback, which is always trusted. Leave empty when the proxy runs on
    /// the same host or the service is exposed directly.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Maximum events buffered in memory. When full, new events get 503 so clients can retry.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    public int MaxBatchSize { get; set; } = 500;

    public int FlushIntervalMs { get; set; } = 1_000;

    /// <summary>Per-IP request limit per minute for all endpoints.</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>Directory for cached RIR files.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>How often each registry file is downloaded again. A failed download is retried after this long too,
    /// unless the registry has no data at all yet (then it is retried hourly).</summary>
    public int GeoRefreshHours { get; set; } = 24;

    public int SiteRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// Key for hashing logged-in user ids on sites with identify_users (at least 32 characters). Keep it out of the
    /// database and stable: changing it gives every user a new visitor id. Empty disables identification.
    /// </summary>
    public string? IdentitySecret { get; set; }
}
