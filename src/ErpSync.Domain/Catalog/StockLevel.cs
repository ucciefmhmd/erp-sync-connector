using ErpSync.Domain.Common;

namespace ErpSync.Domain.Catalog;

/// <summary>On-hand quantity of a product in one warehouse.</summary>
public sealed class StockLevel
{
    private StockLevel()
    {
    }

    public string Sku { get; private set; } = string.Empty;

    public string Warehouse { get; private set; } = string.Empty;

    public decimal QuantityOnHand { get; private set; }

    public DateTimeOffset ErpModifiedAt { get; private set; }

    public DateTimeOffset LastSyncedAt { get; private set; }

    public static StockLevel Create(
        string sku, string warehouse, decimal quantityOnHand,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt) => new()
        {
            Sku = Guard.NotEmpty(sku, nameof(sku), 64),
            Warehouse = Guard.NotEmpty(warehouse, nameof(warehouse), 32),
            QuantityOnHand = quantityOnHand,
            ErpModifiedAt = erpModifiedAt,
            LastSyncedAt = syncedAt,
        };

    /// <returns><c>true</c> when the stored state changed.</returns>
    public bool ApplyUpdate(decimal quantityOnHand, DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        if (erpModifiedAt <= ErpModifiedAt)
        {
            return false;
        }

        QuantityOnHand = quantityOnHand;
        ErpModifiedAt = erpModifiedAt;
        LastSyncedAt = syncedAt;
        return true;
    }
}
