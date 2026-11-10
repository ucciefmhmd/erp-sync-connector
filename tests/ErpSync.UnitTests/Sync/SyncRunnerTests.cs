using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Sync;
using ErpSync.Infrastructure.Persistence;
using ErpSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ErpSync.UnitTests.Support.ErpData;

namespace ErpSync.UnitTests.Sync;

public class SyncRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_run_reads_every_page_and_stores_the_checkpoint()
    {
        await using var app = await TestApp.CreateAsync();
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2), Product(3, 3), Product(4, 4), Product(5, 5)]);

        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Succeeded, summary.Status);
        Assert.Equal(3, summary.Pages); // page size 2
        Assert.Equal(5, summary.Changed);
        Assert.Equal(5, await CountProductsAsync(app));
        var checkpoint = await GetCheckpointAsync(app, SyncEntity.Products);
        Assert.Equal("SKU-005", checkpoint!.LastKey);
    }

    [Fact]
    public async Task Next_run_reads_only_records_changed_after_the_checkpoint()
    {
        await using var app = await TestApp.CreateAsync();
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2), Product(3, 3)]);
        await app.SyncAsync(SyncEntity.Products);

        app.Erp.Products[0] = Product(1, minute: 10, name: "Renamed");
        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(1, summary.Fetched);
        Assert.Equal(1, summary.Changed);
        var name = await app.InScopeAsync(sp => sp.GetRequiredService<ErpSyncDbContext>().Products
            .Where(p => p.Sku == "SKU-001").Select(p => p.Name).SingleAsync(Ct));
        Assert.Equal("Renamed", name);
    }

    [Fact]
    public async Task Invalid_record_is_skipped_without_blocking_the_feed()
    {
        await using var app = await TestApp.CreateAsync();
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2, name: "   "), Product(3, 3)]);

        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Succeeded, summary.Status);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(2, await CountProductsAsync(app));
        Assert.Equal("SKU-003", (await GetCheckpointAsync(app, SyncEntity.Products))!.LastKey);
    }

    [Fact]
    public async Task Failure_keeps_committed_pages_and_the_next_run_resumes_from_there()
    {
        await using var app = await TestApp.CreateAsync();
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2), Product(3, 3), Product(4, 4), Product(5, 5)]);

        // Page 1 is read and committed, then the call for page 2 fails.
        app.Erp.FeedFault = call => call == 2 ? new ErpUnavailableException("ERP went away") : null;
        var failed = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Pages);
        Assert.Equal("SKU-002", (await GetCheckpointAsync(app, SyncEntity.Products))!.LastKey);
        Assert.Equal(2, await CountProductsAsync(app));

        app.Erp.FeedFault = _ => null;
        var resumed = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Succeeded, resumed.Status);
        Assert.Equal(3, resumed.Fetched); // only SKU-003..005 are read again
        Assert.Equal(5, await CountProductsAsync(app));
    }

    [Fact]
    public async Task A_feed_that_breaks_its_ordering_contract_fails_the_run()
    {
        await using var app = await TestApp.CreateAsync();
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2)]);
        app.Erp.ReturnFeedOutOfOrder = true;

        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Failed, summary.Status);
        Assert.Contains("not ordered", summary.Error, StringComparison.Ordinal);
        Assert.Equal(0, await CountProductsAsync(app));
    }

    [Fact]
    public async Task Overlap_window_picks_up_a_change_that_appeared_behind_the_checkpoint()
    {
        await using var app = await TestApp.CreateAsync(sync: o => o.FeedOverlap = TimeSpan.FromMinutes(5));
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2), Product(3, 3)]);
        await app.SyncAsync(SyncEntity.Products);

        // A record becomes visible late, with a timestamp before the checkpoint (minute 3).
        app.Erp.Products.Add(Product(4, minute: 2));
        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Succeeded, summary.Status);
        Assert.Equal(1, summary.Changed); // the re-read records are unchanged and ignored
        Assert.Equal(4, await CountProductsAsync(app));
        Assert.Equal("SKU-003", (await GetCheckpointAsync(app, SyncEntity.Products))!.LastKey); // never moves back
    }

    [Fact]
    public async Task Large_sync_commits_every_page_and_finishes()
    {
        await using var app = await TestApp.CreateAsync(sync: o => o.PageSize = 25);
        app.Erp.Products.AddRange(Enumerable.Range(1, 500).Select(i => Product(i, i)));

        var summary = await app.SyncAsync(SyncEntity.Products);

        Assert.Equal(SyncRunStatus.Succeeded, summary.Status);
        Assert.Equal(20, summary.Pages);
        Assert.Equal(500, await CountProductsAsync(app));
    }

    private static Task<int> CountProductsAsync(TestApp app) =>
        app.InScopeAsync(sp => sp.GetRequiredService<ErpSyncDbContext>().Products.CountAsync(Ct));

    private static Task<SyncCheckpoint?> GetCheckpointAsync(TestApp app, SyncEntity entity) =>
        app.InScopeAsync(sp => sp.GetRequiredService<ErpSyncDbContext>().SyncCheckpoints
            .SingleOrDefaultAsync(c => c.Entity == entity, Ct));
}
