using ErpSync.Domain.Catalog;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;

namespace ErpSync.Application.Abstractions.Persistence;

/// <summary>Commits all tracked changes in one transaction.</summary>
public interface IUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">A unique index was violated.</exception>
    /// <exception cref="ConcurrencyConflictException">Another writer changed the same row first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Drops pending (unsaved) changes after a failed save, keeping unchanged entities tracked.</summary>
    void DiscardChanges();

    /// <summary>Stops tracking everything (after a commit), so long-running batch work doesn't accumulate entities.</summary>
    void ClearTracking();
}

public interface ICatalogRepository
{
    Task<IReadOnlyDictionary<string, Product>> GetProductsAsync(
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<(string Sku, string Warehouse), StockLevel>> GetStockLevelsAsync(
        IReadOnlyCollection<string> skus, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Customer>> GetCustomersAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    void Add(Product product);

    void Add(StockLevel stockLevel);

    void Add(Customer customer);
}

public interface ISalesOrderRepository
{
    Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<SalesOrder?> GetByIdempotencyKeyAsync(string clientName, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Pending orders whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<SalesOrder>> GetDueForDispatchAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>The earliest NextAttemptAt among pending orders, or <c>null</c> when none are pending.</summary>
    Task<DateTimeOffset?> GetNextAttemptAtAsync(CancellationToken cancellationToken);

    void Add(SalesOrder order);
}

public interface ISyncStateRepository
{
    Task<SyncCheckpoint?> GetCheckpointAsync(SyncEntity entity, CancellationToken cancellationToken);

    void Add(SyncCheckpoint checkpoint);

    void Add(SyncRun run);

    /// <summary>Re-attaches a run whose pending changes were discarded, so its final state can be saved.</summary>
    void Update(SyncRun run);

    /// <summary>Starts tracking an already-saved checkpoint again (unchanged), after <see cref="IUnitOfWork.ClearTracking"/>.</summary>
    void Attach(SyncCheckpoint checkpoint);

    /// <summary>Starts tracking an already-saved run again (unchanged), after <see cref="IUnitOfWork.ClearTracking"/>.</summary>
    void Attach(SyncRun run);
}

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException()
    {
    }

    public UniqueConstraintViolationException(string message)
        : base(message)
    {
    }

    public UniqueConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
