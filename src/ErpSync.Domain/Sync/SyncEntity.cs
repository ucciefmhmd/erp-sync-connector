namespace ErpSync.Domain.Sync;

/// <summary>The kinds of ERP data the connector keeps in sync.</summary>
public enum SyncEntity
{
    Products = 1,
    Stock = 2,
    Customers = 3,
}
