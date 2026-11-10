namespace ErpSync.MockErp;

public sealed class MockErpOptions
{
    public const string SectionName = "MockErp";

    public string ClientId { get; set; } = "erpsync-dev";

    public string ClientSecret { get; set; } = "dev-secret-change-me";

    public int TokenLifetimeSeconds { get; set; } = 600;

    /// <summary>Share of API calls answered with 503, to exercise retries and the circuit breaker.</summary>
    public double FailureRate { get; set; } = 0.05;

    /// <summary>Upper bound of random latency added to each API call.</summary>
    public int MaxLatencyMs { get; set; } = 150;

    /// <summary>
    /// Share of order submissions that are saved but answered with HTTP 500, as if the response was
    /// lost. The connector must reconcile these instead of creating duplicates.
    /// </summary>
    public double LostOrderResponseRate { get; set; } = 0.1;

    /// <summary>How often stock and prices change, so incremental sync has something to pick up.</summary>
    public TimeSpan MutationInterval { get; set; } = TimeSpan.FromSeconds(10);
}
