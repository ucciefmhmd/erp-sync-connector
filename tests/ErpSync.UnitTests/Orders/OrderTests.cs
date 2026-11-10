using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Common;
using ErpSync.Application.Orders;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;
using ErpSync.Infrastructure.Persistence;
using ErpSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ErpSync.UnitTests.Support.ErpData;

namespace ErpSync.UnitTests.Orders;

public class OrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replaying_the_same_request_returns_the_original_order()
    {
        await using var app = await SeededAppAsync();

        var first = await CreateAsync(app, Command("key-1", ("SKU-001", 1m)));
        var replay = await CreateAsync(app, Command("key-1", ("SKU-001", 1.000m)));

        Assert.True(first.Value!.Created);
        Assert.False(replay.Value!.Created);
        Assert.Equal(first.Value.OrderId, replay.Value.OrderId);
        Assert.Equal(1, await CountOrdersAsync(app));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_order_is_a_conflict()
    {
        await using var app = await SeededAppAsync();
        await CreateAsync(app, Command("key-1", ("SKU-001", 1m)));

        var result = await CreateAsync(app, Command("key-1", ("SKU-002", 5m)));

        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal("idempotency_key_reused", result.Error.Code);
    }

    [Fact]
    public async Task Unknown_or_inactive_records_are_refused_before_reaching_the_ERP()
    {
        await using var app = await SeededAppAsync();

        var result = await CreateAsync(app, Command("key-1", ("SKU-001", 1m), ("SKU-404", 1m)) with { CustomerCode = "C-BLOCKED" });

        Assert.Equal(ErrorKind.Unprocessable, result.Error!.Kind);
        Assert.True(result.Error.Details!.ContainsKey("customerCode"));
        Assert.True(result.Error.Details.ContainsKey("lines[1].sku"));
        Assert.Equal(0, await CountOrdersAsync(app));
    }

    [Fact]
    public async Task Validation_errors_use_the_clients_field_paths()
    {
        await using var app = await SeededAppAsync();

        var result = await CreateAsync(app, Command("key-1", ("SKU-001", -2m)));

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.True(result.Error.Details!.ContainsKey("lines[0].quantity"));
    }

    [Fact]
    public async Task Due_order_is_submitted_to_the_ERP()
    {
        await using var app = await SeededAppAsync();
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        var result = await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(1, result.Processed);
        Assert.Equal(SalesOrderStatus.Submitted, order.Status);
        Assert.Equal("SO-0001", order.ErpOrderNumber);
        Assert.False(order.OutcomeUnknown);
        Assert.Null(result.NextDueAt);
    }

    [Fact]
    public async Task Rejected_order_fails_without_retrying()
    {
        await using var app = await SeededAppAsync();
        app.Erp.Reject = _ => "Credit limit exceeded";
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Failed, order.Status);
        Assert.Equal("Credit limit exceeded", order.LastError);
        Assert.Null(order.NextAttemptAt);
    }

    [Fact]
    public async Task Transient_failure_schedules_a_retry_and_the_retry_succeeds()
    {
        await using var app = await SeededAppAsync(o => o.BaseRetryDelay = TimeSpan.FromSeconds(10));
        app.Erp.CreateFailures.Enqueue((new ErpUnavailableException("HTTP 503", outcomeUnknown: false), SavedFirst: false));
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        var first = await app.DispatchAsync();
        var afterFailure = await GetOrderAsync(app, id);

        Assert.Equal(SalesOrderStatus.Pending, afterFailure.Status);
        Assert.False(afterFailure.OutcomeUnknown);
        Assert.InRange(afterFailure.NextAttemptAt!.Value - app.Clock.GetUtcNow(), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));
        Assert.Equal(afterFailure.NextAttemptAt, first.NextDueAt);

        Assert.Equal(0, (await app.DispatchAsync()).Processed); // not due yet

        app.Clock.Advance(TimeSpan.FromSeconds(15));
        await app.DispatchAsync();

        var delivered = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Submitted, delivered.Status);
        Assert.Equal(2, delivered.Attempts);
    }

    [Fact]
    public async Task Lost_response_is_reconciled_instead_of_creating_a_duplicate()
    {
        await using var app = await SeededAppAsync();

        // The ERP saves the order but the response never arrives.
        app.Erp.CreateFailures.Enqueue((new ErpUnavailableException("HTTP 500", outcomeUnknown: true), SavedFirst: true));
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        await app.DispatchAsync();
        Assert.True((await GetOrderAsync(app, id)).OutcomeUnknown);

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Submitted, order.Status);
        Assert.Equal("SO-0001", order.ErpOrderNumber);
        Assert.Equal(1, app.Erp.CreateCalls); // never sent twice
        Assert.Equal(1, app.Erp.FindCalls);
        Assert.Single(app.Erp.Orders);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_number_of_attempts()
    {
        await using var app = await SeededAppAsync(o => o.MaxAttempts = 2);
        for (var i = 0; i < 2; i++)
        {
            app.Erp.CreateFailures.Enqueue((new ErpUnavailableException("HTTP 503", outcomeUnknown: false), SavedFirst: false));
        }

        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        await app.DispatchAsync();
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Failed, order.Status);
        Assert.Equal(2, order.Attempts);
    }

    [Fact]
    public async Task A_stale_copy_of_an_order_cannot_be_claimed_twice()
    {
        await using var app = await SeededAppAsync();
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        // Two dispatchers load the same pending order...
        await using var scopeA = await StartScopeAsync(app);
        await using var scopeB = await StartScopeAsync(app);
        var copyA = await scopeA.Db.SalesOrders.SingleAsync(o => o.Id == id, Ct);
        var copyB = await scopeB.Db.SalesOrders.SingleAsync(o => o.Id == id, Ct);

        // ...the first claim wins...
        copyA.BeginAttempt(app.Clock.GetUtcNow().AddMinutes(2));
        await ((IUnitOfWork)scopeA.Db).SaveChangesAsync(Ct);

        // ...and the second is detected by the concurrency token.
        copyB.BeginAttempt(app.Clock.GetUtcNow().AddMinutes(2));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => ((IUnitOfWork)scopeB.Db).SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task An_unknown_outcome_on_the_last_attempt_is_reconciled_not_failed()
    {
        await using var app = await SeededAppAsync(o => o.MaxAttempts = 1);
        app.Erp.CreateFailures.Enqueue((new ErpUnavailableException("HTTP 500", outcomeUnknown: true), SavedFirst: true));
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        await app.DispatchAsync();
        var afterLastAttempt = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Pending, afterLastAttempt.Status); // failing it now could lead to a duplicate later

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Submitted, order.Status);
        Assert.Equal(1, app.Erp.CreateCalls);
    }

    [Fact]
    public async Task Out_of_attempts_fails_only_after_the_ERP_confirms_it_has_no_such_order()
    {
        await using var app = await SeededAppAsync(o => o.MaxAttempts = 1);
        app.Erp.CreateFailures.Enqueue((new ErpUnavailableException("timeout", outcomeUnknown: true), SavedFirst: false));
        var id = (await CreateAsync(app, Command("key-1", ("SKU-001", 2m)))).Value!.OrderId;

        await app.DispatchAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.DispatchAsync();

        var order = await GetOrderAsync(app, id);
        Assert.Equal(SalesOrderStatus.Failed, order.Status);
        Assert.False(order.OutcomeUnknown);
        Assert.Equal(1, app.Erp.CreateCalls); // the reconcile-only attempt never resends
        Assert.Equal(1, app.Erp.FindCalls);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_client()
    {
        await using var app = await SeededAppAsync();

        var fromA = await CreateAsync(app, Command("shared-key", ("SKU-001", 1m)));
        var fromB = await CreateAsync(app, Command("shared-key", ("SKU-002", 3m)) with { ClientName = "client-b" });

        Assert.True(fromA.Value!.Created);
        Assert.True(fromB.Value!.Created);
        Assert.NotEqual(fromA.Value.OrderId, fromB.Value.OrderId);
    }

    private static async Task<TestApp> SeededAppAsync(Action<OrderOptions>? orders = null)
    {
        var app = await TestApp.CreateAsync(orders: orders);
        app.Erp.Products.AddRange([Product(1, 1), Product(2, 2), Product(3, 3, active: false)]);
        app.Erp.Customers.AddRange([Customer("C-001", 1), Customer("C-BLOCKED", 2, active: false)]);
        await app.SyncAsync(SyncEntity.Products);
        await app.SyncAsync(SyncEntity.Customers);
        return app;
    }

    private static CreateSalesOrderCommand Command(string key, params (string Sku, decimal Quantity)[] lines) =>
        new("client-a", key, "C-001", "PO-1", lines.Select(l => new CreateSalesOrderLine(l.Sku, l.Quantity)).ToList());

    private static Task<Result<CreateSalesOrderOutcome>> CreateAsync(TestApp app, CreateSalesOrderCommand command) =>
        app.InScopeAsync(sp => sp.GetRequiredService<CreateSalesOrderHandler>().HandleAsync(command, Ct));

    private static Task<SalesOrder> GetOrderAsync(TestApp app, Guid id) =>
        app.InScopeAsync(sp => sp.GetRequiredService<ErpSyncDbContext>().SalesOrders.AsNoTracking().SingleAsync(o => o.Id == id, Ct));

    private static Task<int> CountOrdersAsync(TestApp app) =>
        app.InScopeAsync(sp => sp.GetRequiredService<ErpSyncDbContext>().SalesOrders.CountAsync(Ct));

    private static Task<DbScope> StartScopeAsync(TestApp app) => Task.FromResult(DbScope.Create(app));

    private sealed class DbScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;

        private DbScope(AsyncServiceScope scope)
        {
            _scope = scope;
            Db = scope.ServiceProvider.GetRequiredService<ErpSyncDbContext>();
        }

        public ErpSyncDbContext Db { get; }

        public static DbScope Create(TestApp app) => new(app.CreateScope());

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}
