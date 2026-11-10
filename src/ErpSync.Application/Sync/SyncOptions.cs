using System.ComponentModel.DataAnnotations;
using ErpSync.Domain.Sync;

namespace ErpSync.Application.Sync;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Records requested per ERP call.</summary>
    [Range(1, 1000)]
    public int PageSize { get; set; } = 200;

    /// <summary>Safety net against an ERP that keeps reporting more pages.</summary>
    [Range(1, 100_000)]
    public int MaxPagesPerRun { get; set; } = 5_000;

    /// <summary>
    /// How far before the checkpoint each run starts re-reading. Use it when the ERP's change
    /// timestamps can become visible out of order; zero trusts the feed completely.
    /// </summary>
    public TimeSpan FeedOverlap { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ProductsInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StockInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan CustomersInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan GetInterval(SyncEntity entity) => entity switch
    {
        SyncEntity.Products => ProductsInterval,
        SyncEntity.Stock => StockInterval,
        SyncEntity.Customers => CustomersInterval,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null),
    };
}
