using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;

namespace ErpSync.Application.Abstractions.Persistence;

/// <summary>Read-only queries, projected straight to DTOs without loading aggregates.</summary>
public interface IReadModelQueries
{
    Task<PagedResult<ProductDto>> ListProductsAsync(ProductListQuery query, CancellationToken cancellationToken);

    Task<ProductDetailsDto?> GetProductAsync(string sku, CancellationToken cancellationToken);

    Task<CustomerDto?> GetCustomerAsync(string code, CancellationToken cancellationToken);

    /// <summary>Returns the order only if it belongs to <paramref name="clientName"/>.</summary>
    Task<SalesOrderDto?> GetSalesOrderAsync(Guid id, string clientName, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncRunDto>> ListSyncRunsAsync(SyncEntity? entity, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncCheckpointDto>> ListCheckpointsAsync(CancellationToken cancellationToken);
}

public sealed record ProductListQuery(int Page, int PageSize, bool? IsActive, DateTimeOffset? ModifiedSince, string? Search);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ProductDto(
    string Sku, string Name, decimal UnitPrice, string Currency, bool IsActive,
    DateTimeOffset ErpModifiedAt, DateTimeOffset LastSyncedAt);

public sealed record StockLevelDto(string Warehouse, decimal QuantityOnHand, DateTimeOffset ErpModifiedAt);

public sealed record ProductDetailsDto(ProductDto Product, IReadOnlyList<StockLevelDto> Stock, decimal TotalOnHand);

public sealed record CustomerDto(
    string Code, string Name, string? Email, decimal Balance, string Currency, bool IsActive,
    DateTimeOffset ErpModifiedAt, DateTimeOffset LastSyncedAt);

public sealed record SalesOrderLineDto(int LineNumber, string Sku, decimal Quantity);

public sealed record SalesOrderDto(
    Guid Id,
    SalesOrderStatus Status,
    string CustomerCode,
    string? CustomerReference,
    string? ErpOrderNumber,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<SalesOrderLineDto> Lines);

public sealed record SyncRunDto(
    Guid Id, SyncEntity Entity, SyncRunStatus Status, string Trigger,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    int Pages, int RecordsFetched, int RecordsChanged, int RecordsSkipped, string? Error, string? TraceId);

public sealed record SyncCheckpointDto(SyncEntity Entity, DateTimeOffset? LastModifiedAt, string? LastKey, DateTimeOffset UpdatedAt);
