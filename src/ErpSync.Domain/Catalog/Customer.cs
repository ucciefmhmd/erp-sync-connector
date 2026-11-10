using ErpSync.Domain.Common;

namespace ErpSync.Domain.Catalog;

/// <summary>A customer account and its open balance as reported by the ERP.</summary>
public sealed class Customer
{
    private Customer()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public decimal Balance { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset ErpModifiedAt { get; private set; }

    public DateTimeOffset LastSyncedAt { get; private set; }

    public static Customer Create(
        string code, string name, string? email, decimal balance, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        var customer = new Customer { Code = Guard.NotEmpty(code, nameof(code), 32) };
        customer.Set(name, email, balance, currency, isActive, erpModifiedAt, syncedAt);
        return customer;
    }

    /// <returns><c>true</c> when the stored state changed.</returns>
    public bool ApplyUpdate(
        string name, string? email, decimal balance, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        if (erpModifiedAt <= ErpModifiedAt)
        {
            return false;
        }

        Set(name, email, balance, currency, isActive, erpModifiedAt, syncedAt);
        return true;
    }

    private void Set(
        string name, string? email, decimal balance, string currency, bool isActive,
        DateTimeOffset erpModifiedAt, DateTimeOffset syncedAt)
    {
        // Validate everything first so a rejected update never leaves the entity half-changed.
        var validName = Guard.NotEmpty(name, nameof(name));
        var validCurrency = Guard.NotEmpty(currency, nameof(currency), 3).ToUpperInvariant();

        Name = validName;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        Balance = balance;
        Currency = validCurrency;
        IsActive = isActive;
        ErpModifiedAt = erpModifiedAt;
        LastSyncedAt = syncedAt;
    }
}
