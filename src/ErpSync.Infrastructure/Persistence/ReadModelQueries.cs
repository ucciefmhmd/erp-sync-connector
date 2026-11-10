using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace ErpSync.Infrastructure.Persistence;

internal sealed class ReadModelQueries(ErpSyncDbContext db) : IReadModelQueries
{
    public async Task<PagedResult<ProductDto>> ListProductsAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var products = db.Products.AsNoTracking();
        if (query.IsActive is { } isActive)
        {
            products = products.Where(p => p.IsActive == isActive);
        }

        if (query.ModifiedSince is { } since)
        {
            products = products.Where(p => p.ErpModifiedAt >= since);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            products = products.Where(p => EF.Functions.Like(p.Name, pattern, "\\") || EF.Functions.Like(p.Sku, pattern, "\\"));
        }

        var total = await products.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await products
            .OrderBy(p => p.Sku)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new ProductDto(p.Sku, p.Name, p.UnitPrice, p.Currency, p.IsActive, p.ErpModifiedAt, p.LastSyncedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedResult<ProductDto>(items, query.Page, query.PageSize, total);
    }

    public async Task<ProductDetailsDto?> GetProductAsync(string sku, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Sku == sku)
            .Select(p => new ProductDto(p.Sku, p.Name, p.UnitPrice, p.Currency, p.IsActive, p.ErpModifiedAt, p.LastSyncedAt))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (product is null)
        {
            return null;
        }

        var stock = await db.StockLevels.AsNoTracking()
            .Where(s => s.Sku == sku)
            .OrderBy(s => s.Warehouse)
            .Select(s => new StockLevelDto(s.Warehouse, s.QuantityOnHand, s.ErpModifiedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new ProductDetailsDto(product, stock, stock.Sum(s => s.QuantityOnHand));
    }

    public Task<CustomerDto?> GetCustomerAsync(string code, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking()
            .Where(c => c.Code == code)
            .Select(c => new CustomerDto(c.Code, c.Name, c.Email, c.Balance, c.Currency, c.IsActive, c.ErpModifiedAt, c.LastSyncedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SalesOrderDto?> GetSalesOrderAsync(Guid id, string clientName, CancellationToken cancellationToken)
    {
        var order = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.ClientName == clientName, cancellationToken).ConfigureAwait(false);
        return order is null
            ? null
            : new SalesOrderDto(
                order.Id, order.Status, order.CustomerCode, order.CustomerReference, order.ErpOrderNumber,
                order.Attempts, order.LastError, order.CreatedAt, order.NextAttemptAt, order.CompletedAt,
                order.Lines.Select(l => new SalesOrderLineDto(l.LineNumber, l.Sku, l.Quantity)).ToList());
    }

    public async Task<IReadOnlyList<SyncRunDto>> ListSyncRunsAsync(SyncEntity? entity, int take, CancellationToken cancellationToken)
    {
        var runs = db.SyncRuns.AsNoTracking();
        if (entity is { } e)
        {
            runs = runs.Where(r => r.Entity == e);
        }

        return await runs
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .Select(r => new SyncRunDto(
                r.Id, r.Entity, r.Status, r.Trigger, r.StartedAt, r.CompletedAt,
                r.Pages, r.RecordsFetched, r.RecordsChanged, r.RecordsSkipped, r.Error, r.TraceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncCheckpointDto>> ListCheckpointsAsync(CancellationToken cancellationToken) =>
        await db.SyncCheckpoints.AsNoTracking()
            .OrderBy(c => c.Entity)
            .Select(c => new SyncCheckpointDto(c.Entity, c.LastModifiedAt, c.LastKey, c.UpdatedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
