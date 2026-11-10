namespace ErpSync.Application.Abstractions.Erp;

/// <summary>A record in an ERP change feed, ordered by (ModifiedAt, Key).</summary>
public interface IErpRecord
{
    string Key { get; }

    DateTimeOffset ModifiedAt { get; }
}

public sealed record ErpPage<T>(IReadOnlyList<T> Items, bool HasMore)
    where T : IErpRecord;

public sealed record ErpProduct(
    string Sku,
    string Name,
    decimal UnitPrice,
    string Currency,
    bool IsActive,
    DateTimeOffset ModifiedAt) : IErpRecord
{
    public string Key => Sku;
}

public sealed record ErpStockLevel(
    string Sku,
    string Warehouse,
    decimal QuantityOnHand,
    DateTimeOffset ModifiedAt) : IErpRecord
{
    public string Key => $"{Sku}|{Warehouse}";
}

public sealed record ErpCustomer(
    string Code,
    string Name,
    string? Email,
    decimal Balance,
    string Currency,
    bool IsActive,
    DateTimeOffset ModifiedAt) : IErpRecord
{
    public string Key => Code;
}

public sealed record ErpSalesOrder(
    string ExternalReference,
    string CustomerCode,
    string? CustomerReference,
    IReadOnlyList<ErpSalesOrderLine> Lines);

public sealed record ErpSalesOrderLine(string Sku, decimal Quantity);

public sealed record ErpOrderConfirmation(string OrderNumber);
