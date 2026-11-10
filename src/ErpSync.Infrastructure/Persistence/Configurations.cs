using ErpSync.Domain.Catalog;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpSync.Infrastructure.Persistence;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Sku);
        builder.Property(p => p.Sku).HasMaxLength(64);
        builder.Property(p => p.Name).HasMaxLength(200);
        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.HasIndex(p => p.ErpModifiedAt);
    }
}

internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        builder.HasKey(s => new { s.Sku, s.Warehouse });
        builder.Property(s => s.Sku).HasMaxLength(64);
        builder.Property(s => s.Warehouse).HasMaxLength(32);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasKey(c => c.Code);
        builder.Property(c => c.Code).HasMaxLength(32);
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(254);
        builder.Property(c => c.Currency).HasMaxLength(3);
    }
}

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.IdempotencyKey).HasMaxLength(100);
        builder.Property(o => o.ClientName).HasMaxLength(64);

        // Idempotency keys are unique per client, so one client can never see or collide with another's orders.
        builder.HasIndex(o => new { o.ClientName, o.IdempotencyKey }).IsUnique();
        builder.Property(o => o.RequestFingerprint).HasMaxLength(128);
        builder.Property(o => o.CustomerCode).HasMaxLength(32);
        builder.Property(o => o.CustomerReference).HasMaxLength(100);
        builder.Property(o => o.ErpOrderNumber).HasMaxLength(64);
        builder.Property(o => o.LastError).HasMaxLength(1000);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(o => o.ExternalReference);

        // Each claim increments Attempts, so it works as an optimistic-concurrency token:
        // two dispatchers can't both claim the same attempt.
        builder.Property(o => o.Attempts).IsConcurrencyToken();

        // Supports the dispatcher's "pending and due" query.
        builder.HasIndex(o => new { o.Status, o.NextAttemptAt });

        builder.OwnsMany(o => o.Lines, line =>
        {
            line.ToTable("SalesOrderLines");
            line.WithOwner().HasForeignKey("SalesOrderId");
            line.HasKey("SalesOrderId", nameof(SalesOrderLine.LineNumber));
            line.Property(l => l.LineNumber).ValueGeneratedNever();
            line.Property(l => l.Sku).HasMaxLength(64);
        });
        builder.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SyncCheckpointConfiguration : IEntityTypeConfiguration<SyncCheckpoint>
{
    public void Configure(EntityTypeBuilder<SyncCheckpoint> builder)
    {
        builder.HasKey(c => c.Entity);
        builder.Property(c => c.Entity).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.LastKey).HasMaxLength(128);
        builder.Ignore(c => c.Position);
    }
}

internal sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Entity).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Trigger).HasMaxLength(32);
        builder.Property(r => r.Error).HasMaxLength(1000);
        builder.Property(r => r.TraceId).HasMaxLength(32);
        builder.HasIndex(r => new { r.Entity, r.StartedAt });
    }
}
