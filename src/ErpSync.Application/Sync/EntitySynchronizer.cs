using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Common;
using ErpSync.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace ErpSync.Application.Sync;

/// <summary>Reads one page of an ERP change feed and applies it to the local store (without saving).</summary>
public interface IEntitySynchronizer
{
    SyncEntity Entity { get; }

    Task<SyncPageResult> SyncPageAsync(SyncPosition? after, int pageSize, CancellationToken cancellationToken);
}

/// <param name="LastPosition">Position of the last record read, or <c>null</c> when the page was empty.</param>
public sealed record SyncPageResult(int Fetched, int Changed, int Skipped, SyncPosition? LastPosition, bool HasMore);

/// <summary>
/// Template for one entity type: fetch a page, verify the feed contract, then upsert record by record.
/// A record that breaks a domain rule is logged and skipped so one bad row cannot block the feed.
/// </summary>
public abstract partial class EntitySynchronizer<TRecord>(TimeProvider clock, ILogger logger) : IEntitySynchronizer
    where TRecord : IErpRecord
{
    public abstract SyncEntity Entity { get; }

    public async Task<SyncPageResult> SyncPageAsync(SyncPosition? after, int pageSize, CancellationToken cancellationToken)
    {
        var page = await FetchAsync(after, pageSize, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0)
        {
            return new SyncPageResult(0, 0, 0, null, false);
        }

        EnsureOrderedAfter(after, page.Items);

        await PrepareAsync(page.Items, cancellationToken).ConfigureAwait(false);

        var syncedAt = clock.GetUtcNow();
        int changed = 0, skipped = 0;
        foreach (var record in page.Items)
        {
            try
            {
                if (Apply(record, syncedAt))
                {
                    changed++;
                }
            }
            catch (DomainException ex)
            {
                skipped++;
                LogRecordSkipped(logger, Entity, record.Key, ex.Message);
            }
        }

        var last = page.Items[^1];
        return new SyncPageResult(page.Items.Count, changed, skipped, new SyncPosition(last.ModifiedAt, last.Key), page.HasMore);
    }

    protected abstract Task<ErpPage<TRecord>> FetchAsync(SyncPosition? after, int limit, CancellationToken cancellationToken);

    /// <summary>Loads the existing local rows for the page in one query.</summary>
    protected abstract Task PrepareAsync(IReadOnlyList<TRecord> records, CancellationToken cancellationToken);

    /// <returns><c>true</c> when the record created or changed a local row.</returns>
    protected abstract bool Apply(TRecord record, DateTimeOffset syncedAt);

    /// <summary>Keyset paging only works if the ERP honours its ordering contract, so check it.</summary>
    private void EnsureOrderedAfter(SyncPosition? after, IReadOnlyList<TRecord> items)
    {
        var previous = after;
        foreach (var item in items)
        {
            var current = new SyncPosition(item.ModifiedAt, item.Key);
            if (current <= previous)
            {
                throw new InvalidOperationException(
                    $"The ERP {Entity} feed is not ordered by (ModifiedAt, Key): '{item.Key}' did not come after '{previous?.Key}'.");
            }

            previous = current;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped invalid {Entity} record {Key}: {Reason}")]
    private static partial void LogRecordSkipped(ILogger logger, SyncEntity entity, string key, string reason);
}
