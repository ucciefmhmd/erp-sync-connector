using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Sync;

namespace ErpSync.Tests.Fakes;

/// <summary>
/// In-memory ERP that follows the same keyset-feed contract as the real adapter, with hooks to
/// inject failures. Shared by the unit and integration tests.
/// </summary>
public sealed class FakeErpClient : IErpClient
{
    private int _orderSequence;

    public List<ErpProduct> Products { get; } = [];

    public List<ErpStockLevel> StockLevels { get; } = [];

    public List<ErpCustomer> Customers { get; } = [];

    /// <summary>Orders the ERP holds, by external reference.</summary>
    public Dictionary<string, string> Orders { get; } = new(StringComparer.Ordinal);

    /// <summary>Called with the 1-based feed call number; return an exception to fail that call.</summary>
    public Func<int, Exception?> FeedFault { get; set; } = _ => null;

    /// <summary>Failures for upcoming order submissions. When <c>SavedFirst</c> is set, the ERP keeps the order anyway.</summary>
    public Queue<(Exception Error, bool SavedFirst)> CreateFailures { get; } = new();

    /// <summary>Returns a rejection reason for an order, or <c>null</c> to accept it.</summary>
    public Func<ErpSalesOrder, string?> Reject { get; set; } = _ => null;

    /// <summary>Breaks the feed contract on purpose by returning records newest first.</summary>
    public bool ReturnFeedOutOfOrder { get; set; }

    public int FeedCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public int FindCalls { get; private set; }

    public Task<ErpPage<ErpProduct>> GetProductsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(ReadFeed(Products, after, limit));

    public Task<ErpPage<ErpStockLevel>> GetStockLevelsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(ReadFeed(StockLevels, after, limit));

    public Task<ErpPage<ErpCustomer>> GetCustomersAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(ReadFeed(Customers, after, limit));

    public Task<ErpOrderConfirmation> CreateSalesOrderAsync(ErpSalesOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        CreateCalls++;

        if (CreateFailures.TryDequeue(out var failure))
        {
            if (failure.SavedFirst)
            {
                Store(order);
            }

            throw failure.Error;
        }

        if (Reject(order) is { } reason)
        {
            throw new ErpRejectedException(reason);
        }

        return Task.FromResult(new ErpOrderConfirmation(Store(order)));
    }

    public Task<ErpOrderConfirmation?> FindSalesOrderAsync(string externalReference, CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(Orders.TryGetValue(externalReference, out var number) ? new ErpOrderConfirmation(number) : null);
    }

    private string Store(ErpSalesOrder order)
    {
        if (!Orders.TryGetValue(order.ExternalReference, out var number))
        {
            number = $"SO-{++_orderSequence:D4}";
            Orders[order.ExternalReference] = number;
        }

        return number;
    }

    private ErpPage<T> ReadFeed<T>(IEnumerable<T> source, SyncPosition? after, int limit)
        where T : IErpRecord
    {
        FeedCalls++;
        if (FeedFault(FeedCalls) is { } failure)
        {
            throw failure;
        }

        var ordered = source
            .Where(r => after is null || new SyncPosition(r.ModifiedAt, r.Key) > after)
            .OrderBy(r => r.ModifiedAt)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var page = ordered.Take(limit).ToList();
        if (ReturnFeedOutOfOrder)
        {
            page.Reverse();
        }

        return new ErpPage<T>(page, ordered.Count > limit);
    }
}
