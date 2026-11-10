using ErpSync.Domain.Common;

namespace ErpSync.Domain.Orders;

/// <summary>
/// A sales order accepted by the API and delivered to the ERP asynchronously (transactional outbox).
/// The aggregate owns the delivery state machine: Pending → Submitted, or Pending → Failed.
/// </summary>
public sealed class SalesOrder
{
    private readonly List<SalesOrderLine> _lines = [];

    private SalesOrder()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The API client that placed the order. Orders and idempotency keys are scoped to it.</summary>
    public string ClientName { get; private set; } = string.Empty;

    /// <summary>Client-supplied key that makes order creation safe to retry.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Hash of the original request, used to detect a key reused with a different payload.</summary>
    public string RequestFingerprint { get; private set; } = string.Empty;

    public string CustomerCode { get; private set; } = string.Empty;

    public string? CustomerReference { get; private set; }

    public IReadOnlyList<SalesOrderLine> Lines => _lines;

    public SalesOrderStatus Status { get; private set; }

    public string? ErpOrderNumber { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>
    /// <c>true</c> when the last attempt may have reached the ERP (for example a timeout).
    /// The next attempt must first check whether the ERP already has the order.
    /// </summary>
    public bool OutcomeUnknown { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The reference sent to the ERP, so the order can be found again after an ambiguous failure.</summary>
    public string ExternalReference => Id.ToString("N");

    public static SalesOrder Create(
        string clientName,
        string idempotencyKey,
        string requestFingerprint,
        string customerCode,
        string? customerReference,
        IEnumerable<(string Sku, decimal Quantity)> lines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var order = new SalesOrder
        {
            Id = Guid.CreateVersion7(now),
            ClientName = Guard.NotEmpty(clientName, nameof(clientName), 64),
            IdempotencyKey = Guard.NotEmpty(idempotencyKey, nameof(idempotencyKey), 100),
            RequestFingerprint = Guard.NotEmpty(requestFingerprint, nameof(requestFingerprint), 128),
            CustomerCode = Guard.NotEmpty(customerCode, nameof(customerCode), 32),
            CustomerReference = string.IsNullOrWhiteSpace(customerReference) ? null : customerReference.Trim(),
            Status = SalesOrderStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
        };

        var lineNumber = 1;
        foreach (var (sku, quantity) in lines)
        {
            order._lines.Add(new SalesOrderLine(lineNumber++, sku, quantity));
        }

        if (order._lines.Count == 0)
        {
            throw new DomainException("An order needs at least one line.");
        }

        return order;
    }

    /// <summary>
    /// Claims the order for one delivery attempt. Until the attempt finishes the outcome is treated
    /// as unknown, and the order only becomes due again when the lease expires. If the process
    /// crashes mid-attempt, the next attempt therefore starts with a reconciliation lookup.
    /// </summary>
    public void BeginAttempt(DateTimeOffset leaseUntil)
    {
        EnsurePending();
        Attempts++;
        OutcomeUnknown = true;
        NextAttemptAt = leaseUntil;
    }

    public void MarkSubmitted(string erpOrderNumber, DateTimeOffset now)
    {
        EnsurePending();
        ErpOrderNumber = Guard.NotEmpty(erpOrderNumber, nameof(erpOrderNumber), 64);
        Status = SalesOrderStatus.Submitted;
        OutcomeUnknown = false;
        LastError = null;
        NextAttemptAt = null;
        CompletedAt = now;
    }

    /// <summary>The ERP refused the order (for example an unknown customer). Retrying will not help.</summary>
    public void MarkRejected(string reason, DateTimeOffset now)
    {
        EnsurePending();
        OutcomeUnknown = false;
        Fail(reason, now);
    }

    /// <summary>
    /// Records a failure that may succeed later, and schedules the next attempt. When the attempts
    /// are used up the order fails, unless the ERP might already have it: then it stays pending so
    /// the next attempt can reconcile, because failing it would invite a duplicate resubmission.
    /// </summary>
    public void RecordTransientFailure(
        string error, bool outcomeUnknown, DateTimeOffset nextAttemptAt, int maxAttempts, DateTimeOffset now)
    {
        EnsurePending();
        OutcomeUnknown = outcomeUnknown;

        if (Attempts >= maxAttempts && !outcomeUnknown)
        {
            Fail($"Gave up after {Attempts} attempts. Last error: {error}", now);
            return;
        }

        LastError = Truncate(error);
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>Whether this attempt may still send the order (as opposed to only reconciling it).</summary>
    public bool CanSend(int maxAttempts) => Attempts <= maxAttempts;

    /// <summary>Fails an order that is out of attempts and confirmed not to be in the ERP.</summary>
    public void GiveUp(DateTimeOffset now)
    {
        EnsurePending();
        if (OutcomeUnknown)
        {
            throw new DomainException("An order the ERP may already have must be reconciled before it is given up.");
        }

        Fail($"Gave up after {Attempts - 1} delivery attempts; the ERP confirmed it does not have the order.", now);
    }

    /// <summary>A reconciliation lookup confirmed the ERP does not have the order, so it is safe to send again.</summary>
    public void ConfirmNotInErp()
    {
        EnsurePending();
        OutcomeUnknown = false;
    }

    private void Fail(string reason, DateTimeOffset now)
    {
        Status = SalesOrderStatus.Failed;
        LastError = Truncate(reason);
        NextAttemptAt = null;
        CompletedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != SalesOrderStatus.Pending)
        {
            throw new DomainException($"Order {Id} is {Status}; only pending orders can be delivered.");
        }
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
