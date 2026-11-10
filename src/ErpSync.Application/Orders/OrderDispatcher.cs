using System.Diagnostics;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Common;
using ErpSync.Domain.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpSync.Application.Orders;

public interface IOrderDispatcher
{
    /// <summary>Delivers one batch of due orders to the ERP.</summary>
    Task<DispatchResult> DispatchDueOrdersAsync(CancellationToken cancellationToken);
}

/// <param name="Processed">Orders attempted in this batch.</param>
/// <param name="NextDueAt">When the next pending order becomes due, if any.</param>
public sealed record DispatchResult(int Processed, DateTimeOffset? NextDueAt);

/// <summary>
/// Delivers outbox orders to the ERP, at most once per order:
/// <list type="number">
/// <item>Claim the order (attempt + lease), saved with an optimistic-concurrency check so two instances can't both send it.</item>
/// <item>If an earlier attempt may have reached the ERP, look the order up first instead of sending it again.</item>
/// <item>Send it; a rejection fails the order, and a transient error schedules a retry with backoff.</item>
/// </list>
/// </summary>
internal sealed partial class OrderDispatcher(
    ISalesOrderRepository orders,
    IUnitOfWork unitOfWork,
    IErpClient erp,
    IOptions<OrderOptions> options,
    TimeProvider clock,
    ILogger<OrderDispatcher> logger) : IOrderDispatcher
{
    public async Task<DispatchResult> DispatchDueOrdersAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var due = await orders.GetDueForDispatchAsync(clock.GetUtcNow(), settings.DispatchBatchSize, cancellationToken)
            .ConfigureAwait(false);

        var processed = 0;
        foreach (var order in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DispatchAsync(order, settings, cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
        }

        var nextDueAt = await orders.GetNextAttemptAtAsync(cancellationToken).ConfigureAwait(false);
        return new DispatchResult(processed, nextDueAt);
    }

    private async Task<bool> DispatchAsync(SalesOrder order, OrderOptions settings, CancellationToken cancellationToken)
    {
        // Each delivery is its own trace, propagated to the ERP call via the W3C traceparent header.
        using var activity = ErpSyncTelemetry.StartRootActivity("dispatch sales order");
        activity.SetTag("erpsync.order_id", order.Id);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = order.Id });

        var needsReconciliation = order.OutcomeUnknown;

        // 1. Claim. If another instance claimed it first, the concurrency check fails and we skip it.
        order.BeginAttempt(clock.GetUtcNow() + settings.AttemptLease);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            unitOfWork.DiscardChanges();
            LogClaimLost(logger, order.Id);
            return false;
        }

        // 2. Deliver.
        var outcome = await DeliverAsync(order, needsReconciliation, settings, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        ErpSyncTelemetry.OrdersDispatched.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        activity.SetTag("erpsync.outcome", outcome);
        if (order.Status == SalesOrderStatus.Failed)
        {
            activity.SetStatus(ActivityStatusCode.Error, order.LastError);
        }

        return true;
    }

    private async Task<string> DeliverAsync(
        SalesOrder order, bool needsReconciliation, OrderOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            if (needsReconciliation)
            {
                var existing = await erp.FindSalesOrderAsync(order.ExternalReference, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    order.MarkSubmitted(existing.OrderNumber, clock.GetUtcNow());
                    LogReconciled(logger, order.Id, existing.OrderNumber);
                    return "reconciled";
                }

                order.ConfirmNotInErp();
            }

            // Out of attempts: this attempt only existed to reconcile, and the ERP doesn't have the order.
            if (!order.CanSend(settings.MaxAttempts))
            {
                order.GiveUp(clock.GetUtcNow());
                LogGaveUp(logger, order.Id, order.Attempts - 1, "confirmed not in the ERP after the last attempt");
                return "gave_up";
            }

            var confirmation = await erp.CreateSalesOrderAsync(ToErpOrder(order), cancellationToken).ConfigureAwait(false);
            order.MarkSubmitted(confirmation.OrderNumber, clock.GetUtcNow());
            LogSubmitted(logger, order.Id, confirmation.OrderNumber, order.Attempts);
            return "submitted";
        }
        catch (ErpRejectedException ex)
        {
            order.MarkRejected(ex.Message, clock.GetUtcNow());
            LogRejected(logger, order.Id, ex.Message);
            return "rejected";
        }
        catch (ErpUnavailableException ex)
        {
            ScheduleRetry(order, ex.Message, ex.OutcomeUnknown, settings);
            return order.Status == SalesOrderStatus.Failed ? "gave_up" : "retry";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-call: we can't know whether the ERP got the order.
            ScheduleRetry(order, "Interrupted by shutdown.", outcomeUnknown: true, settings);
            return "interrupted";
        }
#pragma warning disable CA1031 // An unexpected error must not crash the dispatcher; the order is retried cautiously.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The full exception goes to the logs only; the order (visible to API clients) gets a neutral message.
            LogUnexpectedError(logger, ex, order.Id);
            ScheduleRetry(order, $"Unexpected error during delivery (trace {Activity.Current?.TraceId}).", outcomeUnknown: true, settings);
            return "error";
        }
    }

    private void ScheduleRetry(SalesOrder order, string error, bool outcomeUnknown, OrderOptions settings)
    {
        var delay = RetryBackoff.Compute(order.Attempts, settings.BaseRetryDelay, settings.MaxRetryDelay);
        var now = clock.GetUtcNow();
        order.RecordTransientFailure(error, outcomeUnknown, now + delay, settings.MaxAttempts, now);

        if (order.Status == SalesOrderStatus.Failed)
        {
            LogGaveUp(logger, order.Id, order.Attempts, error);
        }
        else
        {
            LogRetryScheduled(logger, order.Id, order.Attempts, delay, error);
        }
    }

    private static ErpSalesOrder ToErpOrder(SalesOrder order) => new(
        order.ExternalReference,
        order.CustomerCode,
        order.CustomerReference,
        order.Lines.Select(l => new ErpSalesOrderLine(l.Sku, l.Quantity)).ToList());

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} submitted to the ERP as {ErpOrderNumber} (attempt {Attempt})")]
    private static partial void LogSubmitted(ILogger logger, Guid orderId, string erpOrderNumber, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} was already in the ERP as {ErpOrderNumber}; marked submitted without resending")]
    private static partial void LogReconciled(ILogger logger, Guid orderId, string erpOrderNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} was rejected by the ERP: {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid orderId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} attempt {Attempt} failed; retrying in {Delay}. Error: {Error}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid orderId, int attempt, TimeSpan delay, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order {OrderId} failed permanently after {Attempts} attempts. Last error: {Error}")]
    private static partial void LogGaveUp(ILogger logger, Guid orderId, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Order {OrderId} was claimed by another dispatcher; skipping")]
    private static partial void LogClaimLost(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while delivering order {OrderId}")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, Guid orderId);
}
