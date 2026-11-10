using ErpSync.Application.Sync;
using ErpSync.Domain.Sync;
using Microsoft.Extensions.Options;

namespace ErpSync.Api.Workers;

public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    /// <summary>Turn off to run the API without background processing (for example in tests).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Runs each entity's sync on its own interval, and immediately when one is requested through the API.
/// Runs are sequential, which keeps the load on the ERP predictable.
/// </summary>
internal sealed partial class SyncWorker(
    IServiceScopeFactory scopeFactory,
    SyncRequestChannel requests,
    IOptions<SyncOptions> options,
    TimeProvider clock,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextDue = Enum.GetValues<SyncEntity>().ToDictionary(e => e, _ => clock.GetUtcNow());

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var entity in nextDue.Keys.ToList())
            {
                if (nextDue[entity] <= clock.GetUtcNow())
                {
                    await RunAsync(entity, "schedule", stoppingToken).ConfigureAwait(false);
                    nextDue[entity] = clock.GetUtcNow() + options.Value.GetInterval(entity);
                }
            }

            while (requests.TryDequeue(out var requested))
            {
                await RunAsync(requested, "manual", stoppingToken).ConfigureAwait(false);
                nextDue[requested] = clock.GetUtcNow() + options.Value.GetInterval(requested);
            }

            var wait = nextDue.Values.Min() - clock.GetUtcNow();
            await WaitForNextWorkAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForNextWorkAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(wait, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            await requests.WaitForRequestAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The next scheduled run is due.
        }
    }

    private async Task RunAsync(SyncEntity entity, string trigger, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<ISyncRunner>();
            await runner.RunAsync(entity, trigger, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // The worker must survive anything a single run throws (for example the database being unavailable).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunCrashed(logger, ex, entity);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync {Entity} could not run; it will be retried at its next interval")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, SyncEntity entity);
}
