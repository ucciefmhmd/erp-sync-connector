using System.Threading.Channels;

namespace ErpSync.Application.Orders;

/// <summary>Wakes the order dispatcher as soon as a new order is accepted, instead of waiting for the next poll.</summary>
public sealed class OrderDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Waits until notified or until <paramref name="timeout"/> elapses.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        using var timeoutSource = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
            _channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: time for a regular poll.
        }
    }
}
