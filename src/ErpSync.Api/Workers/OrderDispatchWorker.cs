using ErpSync.Application.Orders;
using Microsoft.Extensions.Options;

namespace ErpSync.Api.Workers;

/// <summary>Delivers outbox orders to the ERP. Wakes up on new orders, otherwise polls for due retries.</summary>
internal sealed partial class OrderDispatchWorker(
    IServiceScopeFactory scopeFactory,
    OrderDispatchSignal signal,
    IOptions<OrderOptions> options,
    TimeProvider clock,
    ILogger<OrderDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            var result = new DispatchResult(0, null);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOrderDispatcher>();
                result = await dispatcher.DispatchDueOrdersAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Keep the worker alive; the next poll retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogDispatchCrashed(logger, ex);
            }

            // A full batch means more orders are probably due, so go again straight away. Otherwise
            // sleep until the next retry is due (capped by the poll interval) or a new order arrives.
            if (result.Processed < settings.DispatchBatchSize)
            {
                var wait = settings.DispatchPollInterval;
                if (result.NextDueAt is { } nextDueAt)
                {
                    var untilDue = nextDueAt - clock.GetUtcNow();
                    wait = untilDue < wait ? (untilDue > TimeSpan.Zero ? untilDue : TimeSpan.FromMilliseconds(200)) : wait;
                }

                await signal.WaitAsync(wait, clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Order dispatch failed; retrying at the next poll")]
    private static partial void LogDispatchCrashed(ILogger logger, Exception exception);
}
