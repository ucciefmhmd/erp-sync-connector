using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Domain.Catalog;
using ErpSync.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace ErpSync.Application.Sync;

internal sealed class ProductSynchronizer(
    IErpClient erp, ICatalogRepository catalog, TimeProvider clock, ILogger<ProductSynchronizer> logger)
    : EntitySynchronizer<ErpProduct>(clock, logger)
{
    private Dictionary<string, Product> _existing = new(StringComparer.Ordinal);

    public override SyncEntity Entity => SyncEntity.Products;

    protected override Task<ErpPage<ErpProduct>> FetchAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        erp.GetProductsAsync(after, limit, cancellationToken);

    protected override async Task PrepareAsync(IReadOnlyList<ErpProduct> records, CancellationToken cancellationToken)
    {
        var skus = records.Select(r => r.Sku).Distinct(StringComparer.Ordinal).ToList();
        var existing = await catalog.GetProductsAsync(skus, cancellationToken).ConfigureAwait(false);
        _existing = new Dictionary<string, Product>(existing, StringComparer.Ordinal);
    }

    protected override bool Apply(ErpProduct record, DateTimeOffset syncedAt)
    {
        if (_existing.TryGetValue(record.Sku, out var product))
        {
            return product.ApplyUpdate(record.Name, record.UnitPrice, record.Currency, record.IsActive, record.ModifiedAt, syncedAt);
        }

        product = Product.Create(record.Sku, record.Name, record.UnitPrice, record.Currency, record.IsActive, record.ModifiedAt, syncedAt);
        catalog.Add(product);
        _existing[record.Sku] = product;
        return true;
    }
}

internal sealed class StockSynchronizer(
    IErpClient erp, ICatalogRepository catalog, TimeProvider clock, ILogger<StockSynchronizer> logger)
    : EntitySynchronizer<ErpStockLevel>(clock, logger)
{
    private Dictionary<(string Sku, string Warehouse), StockLevel> _existing = [];

    public override SyncEntity Entity => SyncEntity.Stock;

    protected override Task<ErpPage<ErpStockLevel>> FetchAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        erp.GetStockLevelsAsync(after, limit, cancellationToken);

    protected override async Task PrepareAsync(IReadOnlyList<ErpStockLevel> records, CancellationToken cancellationToken)
    {
        var skus = records.Select(r => r.Sku).Distinct(StringComparer.Ordinal).ToList();
        var existing = await catalog.GetStockLevelsAsync(skus, cancellationToken).ConfigureAwait(false);
        _existing = new Dictionary<(string, string), StockLevel>(existing);
    }

    protected override bool Apply(ErpStockLevel record, DateTimeOffset syncedAt)
    {
        var key = (record.Sku, record.Warehouse);
        if (_existing.TryGetValue(key, out var level))
        {
            return level.ApplyUpdate(record.QuantityOnHand, record.ModifiedAt, syncedAt);
        }

        level = StockLevel.Create(record.Sku, record.Warehouse, record.QuantityOnHand, record.ModifiedAt, syncedAt);
        catalog.Add(level);
        _existing[key] = level;
        return true;
    }
}

internal sealed class CustomerSynchronizer(
    IErpClient erp, ICatalogRepository catalog, TimeProvider clock, ILogger<CustomerSynchronizer> logger)
    : EntitySynchronizer<ErpCustomer>(clock, logger)
{
    private Dictionary<string, Customer> _existing = new(StringComparer.Ordinal);

    public override SyncEntity Entity => SyncEntity.Customers;

    protected override Task<ErpPage<ErpCustomer>> FetchAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        erp.GetCustomersAsync(after, limit, cancellationToken);

    protected override async Task PrepareAsync(IReadOnlyList<ErpCustomer> records, CancellationToken cancellationToken)
    {
        var codes = records.Select(r => r.Code).Distinct(StringComparer.Ordinal).ToList();
        var existing = await catalog.GetCustomersAsync(codes, cancellationToken).ConfigureAwait(false);
        _existing = new Dictionary<string, Customer>(existing, StringComparer.Ordinal);
    }

    protected override bool Apply(ErpCustomer record, DateTimeOffset syncedAt)
    {
        if (_existing.TryGetValue(record.Code, out var customer))
        {
            return customer.ApplyUpdate(
                record.Name, record.Email, record.Balance, record.Currency, record.IsActive, record.ModifiedAt, syncedAt);
        }

        customer = Customer.Create(
            record.Code, record.Name, record.Email, record.Balance, record.Currency, record.IsActive, record.ModifiedAt, syncedAt);
        catalog.Add(customer);
        _existing[record.Code] = customer;
        return true;
    }
}
