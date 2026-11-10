using System.ComponentModel.DataAnnotations;

namespace ErpSync.Application.Orders;

public sealed class OrderOptions
{
    public const string SectionName = "Orders";

    /// <summary>Delivery attempts before an order is parked as Failed.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a claimed order is reserved for one attempt. Must exceed the worst-case time of one
    /// delivery (a reconciliation lookup plus the create call, each with its timeouts and a possible token refresh).
    /// </summary>
    public TimeSpan AttemptLease { get; set; } = TimeSpan.FromMinutes(5);

    [Range(1, 500)]
    public int DispatchBatchSize { get; set; } = 25;

    /// <summary>Fallback polling interval; new orders also wake the dispatcher immediately.</summary>
    public TimeSpan DispatchPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Reject orders for unknown or inactive customers and products using the synced data.</summary>
    public bool ValidateAgainstCatalog { get; set; } = true;
}
