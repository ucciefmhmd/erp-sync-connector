using System.Diagnostics;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Common;
using ErpSync.Domain.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpSync.Application.Sync;

public interface ISyncRunner
{
    Task<SyncRunSummary> RunAsync(SyncEntity entity, string trigger, CancellationToken cancellationToken);
}

public sealed record SyncRunSummary(
    Guid RunId, SyncEntity Entity, SyncRunStatus Status, int Pages, int Fetched, int Changed, int Skipped, string? Error);

/// <summary>
/// Runs one incremental sync for an entity type. Each page is committed together with the
/// checkpoint, so a crash or failure resumes from the last committed page instead of starting over.
/// </summary>
internal sealed partial class SyncRunner(
    IEnumerable<IEntitySynchronizer> synchronizers,
    ISyncStateRepository syncState,
    IUnitOfWork unitOfWork,
    IOptions<SyncOptions> options,
    TimeProvider clock,
    ILogger<SyncRunner> logger) : ISyncRunner
{
    public async Task<SyncRunSummary> RunAsync(SyncEntity entity, string trigger, CancellationToken cancellationToken)
    {
        var synchronizer = synchronizers.SingleOrDefault(s => s.Entity == entity)
            ?? throw new InvalidOperationException($"No synchronizer is registered for {entity}.");

        // Every run gets its own trace id (even without a tracing exporter attached), which ties
        // together its log lines, its outgoing ERP calls (W3C traceparent) and its SyncRun record.
        using var activity = ErpSyncTelemetry.StartRootActivity($"sync {entity}");
        activity.SetTag("erpsync.entity", entity.ToString());
        activity.SetTag("erpsync.trigger", trigger);

        var checkpoint = await syncState.GetCheckpointAsync(entity, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            checkpoint = SyncCheckpoint.Start(entity, clock.GetUtcNow());
            syncState.Add(checkpoint);
        }

        var run = SyncRun.Start(entity, trigger, activity.TraceId.ToString(), clock.GetUtcNow());
        syncState.Add(run);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRunStarted(logger, entity, run.Id, trigger, checkpoint.LastModifiedAt);

        try
        {
            var settings = options.Value;
            var position = StartPosition(checkpoint.Position, settings.FeedOverlap);

            for (var page = 1; ; page++)
            {
                if (page > settings.MaxPagesPerRun)
                {
                    throw new InvalidOperationException(
                        $"Stopped after {settings.MaxPagesPerRun} pages; the ERP kept reporting more data.");
                }

                var result = await synchronizer.SyncPageAsync(position, settings.PageSize, cancellationToken).ConfigureAwait(false);

                run.RecordPage(result.Fetched, result.Changed, result.Skipped);

                // Pages inside the overlap window are re-reads; the checkpoint only ever moves forward.
                if (result.LastPosition is not null && result.LastPosition > checkpoint.Position)
                {
                    checkpoint.Advance(result.LastPosition, clock.GetUtcNow());
                }

                // Page data, checkpoint and run progress are committed atomically. If the save fails,
                // DiscardChanges() rolls all three back to the last committed page.
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                position = result.LastPosition ?? position;

                // Keep memory flat on large syncs: forget the page's entities, keep tracking run and checkpoint.
                unitOfWork.ClearTracking();
                syncState.Attach(checkpoint);
                syncState.Attach(run);

                ErpSyncTelemetry.RecordsChanged.Add(result.Changed, new KeyValuePair<string, object?>("entity", entity.ToString()));
                ErpSyncTelemetry.RecordsSkipped.Add(result.Skipped, new KeyValuePair<string, object?>("entity", entity.ToString()));

                if (!result.HasMore)
                {
                    break;
                }
            }

            run.Succeed(clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogRunSucceeded(logger, entity, run.Id, run.Pages, run.RecordsFetched, run.RecordsChanged, run.RecordsSkipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(run, "Cancelled (host shutting down).").ConfigureAwait(false);
            throw;
        }
#pragma warning disable CA1031 // A failed run is recorded and reported; the worker keeps running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErpSyncTelemetry.SyncRunsFailed.Add(1, new KeyValuePair<string, object?>("entity", entity.ToString()));
            LogRunFailed(logger, ex, entity, run.Id, run.Pages);
            await RecordFailureAsync(run, ex.Message).ConfigureAwait(false);
        }

        return new SyncRunSummary(
            run.Id, entity, run.Status, run.Pages, run.RecordsFetched, run.RecordsChanged, run.RecordsSkipped, run.Error);
    }

    /// <summary>
    /// Where to start reading. With an overlap configured, the run re-reads a short window before the
    /// checkpoint. This protects against ERPs where a change can become visible with a timestamp
    /// earlier than records already read (long transactions, clock skew). Upserts are idempotent,
    /// so re-reading costs a few records, not correctness.
    /// </summary>
    private static SyncPosition? StartPosition(SyncPosition? checkpoint, TimeSpan overlap) =>
        checkpoint is null || overlap <= TimeSpan.Zero
            ? checkpoint
            : new SyncPosition(checkpoint.ModifiedAt - overlap, string.Empty);

    /// <summary>
    /// Saves the failed run on its own. Changes from the failing page are discarded; pages that were
    /// already committed (and the checkpoint that goes with them) are kept.
    /// </summary>
    private async Task RecordFailureAsync(SyncRun run, string error)
    {
        try
        {
            unitOfWork.DiscardChanges();
            run.Fail(error, clock.GetUtcNow());
            syncState.Update(run);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Never let bookkeeping hide the original failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunBookkeepingFailed(logger, ex, run.Entity, run.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Sync {Entity} started (run {RunId}, trigger {Trigger}, checkpoint {Checkpoint})")]
    private static partial void LogRunStarted(ILogger logger, SyncEntity entity, Guid runId, string trigger, DateTimeOffset? checkpoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Sync {Entity} succeeded (run {RunId}): {Pages} pages, {Fetched} fetched, {Changed} changed, {Skipped} skipped")]
    private static partial void LogRunSucceeded(
        ILogger logger, SyncEntity entity, Guid runId, int pages, int fetched, int changed, int skipped);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Sync {Entity} failed (run {RunId}) after {Pages} committed pages; it resumes from the last checkpoint")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, SyncEntity entity, Guid runId, int pages);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the failure of sync {Entity} run {RunId}")]
    private static partial void LogRunBookkeepingFailed(ILogger logger, Exception exception, SyncEntity entity, Guid runId);
}
