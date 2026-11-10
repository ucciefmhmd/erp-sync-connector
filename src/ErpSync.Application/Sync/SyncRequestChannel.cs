using System.Collections.Concurrent;
using System.Threading.Channels;
using ErpSync.Domain.Sync;

namespace ErpSync.Application.Sync;

/// <summary>
/// Lets the API ask the background worker for an immediate sync. Requests for an entity that is
/// already queued are merged, so repeated clicks cannot pile up work.
/// </summary>
public sealed class SyncRequestChannel
{
    private readonly Channel<SyncEntity> _channel = Channel.CreateBounded<SyncEntity>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<SyncEntity, byte> _queued = new();

    /// <returns><c>false</c> if the queue is full.</returns>
    public bool TryRequest(SyncEntity entity)
    {
        if (!_queued.TryAdd(entity, 0))
        {
            return true;
        }

        if (_channel.Writer.TryWrite(entity))
        {
            return true;
        }

        _queued.TryRemove(entity, out _);
        return false;
    }

    public bool TryDequeue(out SyncEntity entity)
    {
        if (_channel.Reader.TryRead(out entity))
        {
            _queued.TryRemove(entity, out _);
            return true;
        }

        return false;
    }

    public ValueTask<bool> WaitForRequestAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);
}
