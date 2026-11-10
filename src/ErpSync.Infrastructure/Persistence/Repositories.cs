using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Domain.Catalog;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace ErpSync.Infrastructure.Persistence;

internal sealed class CatalogRepository(ErpSyncDbContext db) : ICatalogRepository
{
    public async Task<IReadOnlyDictionary<string, Product>> GetProductsAsync(
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken) =>
        await db.Products.Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<(string Sku, string Warehouse), StockLevel>> GetStockLevelsAsync(
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken) =>
        await db.StockLevels.Where(s => skus.Contains(s.Sku))
            .ToDictionaryAsync(s => (s.Sku, s.Warehouse), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, Customer>> GetCustomersAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Customers.Where(c => codes.Contains(c.Code))
            .ToDictionaryAsync(c => c.Code, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

    public void Add(Product product) => db.Products.Add(product);

    public void Add(StockLevel stockLevel) => db.StockLevels.Add(stockLevel);

    public void Add(Customer customer) => db.Customers.Add(customer);
}

internal sealed class SalesOrderRepository(ErpSyncDbContext db) : ISalesOrderRepository
{
    public Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.SalesOrders.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<SalesOrder?> GetByIdempotencyKeyAsync(string clientName, string idempotencyKey, CancellationToken cancellationToken) =>
        db.SalesOrders.SingleOrDefaultAsync(o => o.ClientName == clientName && o.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<SalesOrder>> GetDueForDispatchAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        await db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Pending && o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<DateTimeOffset?> GetNextAttemptAtAsync(CancellationToken cancellationToken) =>
        db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Pending)
            .OrderBy(o => o.NextAttemptAt)
            .Select(o => o.NextAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(SalesOrder order) => db.SalesOrders.Add(order);
}

internal sealed class SyncStateRepository(ErpSyncDbContext db) : ISyncStateRepository
{
    public Task<SyncCheckpoint?> GetCheckpointAsync(SyncEntity entity, CancellationToken cancellationToken) =>
        db.SyncCheckpoints.SingleOrDefaultAsync(c => c.Entity == entity, cancellationToken);

    public void Add(SyncCheckpoint checkpoint) => db.SyncCheckpoints.Add(checkpoint);

    public void Add(SyncRun run) => db.SyncRuns.Add(run);

    public void Update(SyncRun run) => db.SyncRuns.Update(run);

    public void Attach(SyncCheckpoint checkpoint) => db.SyncCheckpoints.Attach(checkpoint);

    public void Attach(SyncRun run) => db.SyncRuns.Attach(run);
}
