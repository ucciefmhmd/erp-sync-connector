using ErpSync.Domain.Sync;

namespace ErpSync.Application.Abstractions.Erp;

/// <summary>
/// Port to the ERP. The application only depends on this contract; the HTTP adapter, its
/// authentication and its resilience policies live in the infrastructure layer.
/// </summary>
public interface IErpClient
{
    Task<ErpPage<ErpProduct>> GetProductsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken);

    Task<ErpPage<ErpStockLevel>> GetStockLevelsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken);

    Task<ErpPage<ErpCustomer>> GetCustomersAsync(SyncPosition? after, int limit, CancellationToken cancellationToken);

    /// <summary>Creates a sales order. The order's external reference doubles as the idempotency key.</summary>
    /// <exception cref="ErpRejectedException">The ERP refused the order; retrying will not help.</exception>
    /// <exception cref="ErpUnavailableException">The ERP could not be reached or failed; retry later.</exception>
    Task<ErpOrderConfirmation> CreateSalesOrderAsync(ErpSalesOrder order, CancellationToken cancellationToken);

    /// <summary>Looks up an order by the reference we sent. Used after an ambiguous failure.</summary>
    Task<ErpOrderConfirmation?> FindSalesOrderAsync(string externalReference, CancellationToken cancellationToken);
}
