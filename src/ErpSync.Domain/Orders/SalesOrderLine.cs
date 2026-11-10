using ErpSync.Domain.Common;

namespace ErpSync.Domain.Orders;

public sealed class SalesOrderLine
{
    private SalesOrderLine()
    {
    }

    internal SalesOrderLine(int lineNumber, string sku, decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be greater than zero.");
        }

        LineNumber = lineNumber;
        Sku = Guard.NotEmpty(sku, nameof(sku), 64);
        Quantity = quantity;
    }

    public int LineNumber { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }
}
