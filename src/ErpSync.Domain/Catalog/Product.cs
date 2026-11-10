using ErpSync.Domain.Common;

namespace ErpSync.Domain.Catalog;

/// <summary>A product as last received from the ERP. The ERP is the system of record.</summary>
public sealed class Product
{
    private Product()
    {
    }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    /// <summary>Last modification timestamp reported by the ERP.</summary>
    public DateTimeOffset ErpModifiedAt { get; private set; }

    public DateTimeOffset LastSyncedAt { get; private set; }

    public static Product Create(
        string sku, string name, decimal unitPrice, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        var product = new Product { Sku = Guard.NotEmpty(sku, nameof(sku), 64) };
        product.Set(name, unitPrice, currency, isActive, erpModifiedAt, syncedAt);
        return product;
    }

    /// <summary>
    /// Applies an ERP update. Updates older than the stored version are ignored, which makes
    /// re-delivered or out-of-order pages harmless.
    /// </summary>
    /// <returns><c>true</c> when the stored state changed.</returns>
    public bool ApplyUpdate(
        string name, decimal unitPrice, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        if (erpModifiedAt <= ErpModifiedAt)
        {
            return false;
        }

        Set(name, unitPrice, currency, isActive, erpModifiedAt, syncedAt);
        return true;
    }

    private void Set(
        string name, decimal unitPrice, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        // Validate everything first so a rejected update never leaves the entity half-changed.
        var validName = Guard.NotEmpty(name, nameof(name));
        var validPrice = Guard.NotNegative(unitPrice, nameof(unitPrice));
        var validCurrency = Guard.NotEmpty(currency, nameof(currency), 3).ToUpperInvariant();

        Name = validName;
        UnitPrice = validPrice;
        Currency = validCurrency;
        IsActive = isActive;
        ErpModifiedAt = erpModifiedAt;
        LastSyncedAt = syncedAt;
    }
}
