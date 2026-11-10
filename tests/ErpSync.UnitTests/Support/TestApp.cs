using ErpSync.Application;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Orders;
using ErpSync.Application.Sync;
using ErpSync.Domain.Sync;
using ErpSync.Infrastructure;
using ErpSync.Infrastructure.Persistence;
using ErpSync.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ErpSync.UnitTests.Support;

/// <summary>
/// The real application services and EF Core mappings on an in-memory SQLite database, with a fake
/// ERP and a controllable clock. Fast, but exercises real queries, transactions and concurrency tokens.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    private TestApp(SqliteConnection connection, ServiceProvider services, FakeErpClient erp, FakeTimeProvider clock)
    {
        _connection = connection;
        _services = services;
        Erp = erp;
        Clock = clock;
    }

    public FakeErpClient Erp { get; }

    public FakeTimeProvider Clock { get; }

    public static async Task<TestApp> CreateAsync(Action<SyncOptions>? sync = null, Action<OrderOptions>? orders = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var erp = new FakeErpClient();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddApplication()
            .AddPersistence(options => options.UseSqlite(connection))
            .AddSingleton<IErpClient>(erp)
            .AddSingleton<TimeProvider>(clock)
            .Configure<SyncOptions>(o =>
            {
                o.PageSize = 2;
                o.FeedOverlap = TimeSpan.Zero; // tests that need it switch it on explicitly
                sync?.Invoke(o);
            })
            .Configure<OrderOptions>(o => orders?.Invoke(o))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ErpSyncDbContext>().Database
                .MigrateAsync(TestContext.Current.CancellationToken);
        }

        return new TestApp(connection, services, erp, clock);
    }

    public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

    /// <summary>Runs <paramref name="action"/> in a fresh DI scope, like one request or one worker iteration.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<SyncRunSummary> SyncAsync(SyncEntity entity) =>
        InScopeAsync(sp => sp.GetRequiredService<ISyncRunner>().RunAsync(entity, "test", TestContext.Current.CancellationToken));

    public Task<DispatchResult> DispatchAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<IOrderDispatcher>().DispatchDueOrdersAsync(TestContext.Current.CancellationToken));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
