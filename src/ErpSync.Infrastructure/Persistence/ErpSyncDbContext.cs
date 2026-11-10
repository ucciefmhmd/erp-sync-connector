using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Domain.Catalog;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpSync.Infrastructure.Persistence;

public sealed class ErpSyncDbContext(DbContextOptions<ErpSyncDbContext> options) : DbContext(options), IUnitOfWork
{
    // SQLite result codes: https://www.sqlite.org/rescode.html
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<StockLevel> StockLevels => Set<StockLevel>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();

    public DbSet<SyncCheckpoint> SyncCheckpoints => Set<SyncCheckpoint>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The row was changed by another writer.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException
        {
            SqliteErrorCode: SqliteConstraint,
            SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        })
        {
            throw new UniqueConstraintViolationException("A row with the same key already exists.", ex);
        }
    }

    public void DiscardChanges()
    {
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    public void ClearTracking() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ErpSyncDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite cannot compare or sort DateTimeOffset values, so store them as UTC ticks (full precision).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }
}
