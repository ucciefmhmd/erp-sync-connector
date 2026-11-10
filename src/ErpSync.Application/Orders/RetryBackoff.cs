namespace ErpSync.Application.Orders;

/// <summary>Exponential backoff with ±20% jitter, so many failed orders don't all retry at the same moment.</summary>
public static class RetryBackoff
{
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay) =>
        Compute(attempt, baseDelay, maxDelay, Random.Shared.NextDouble());

    /// <param name="random">A value in [0, 1); exposed so tests can be deterministic.</param>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var exponent = Math.Min(attempt - 1, 30);
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, maxDelay.TotalMilliseconds);
        var jitter = 0.8 + (0.4 * random);
        return TimeSpan.FromMilliseconds(capped * jitter);
    }
}
