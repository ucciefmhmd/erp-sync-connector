using System.Security.Cryptography;
using System.Text;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Orders;
using ErpSync.Application.Sync;
using ErpSync.Domain.Sync;
using ErpSync.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ErpSync.IntegrationTests;

/// <summary>
/// Hosts the real API (middleware, auth, endpoints, EF Core migrations on a temporary SQLite file)
/// with the ERP replaced by an in-memory fake. Background workers are off; tests drive them explicitly.
/// </summary>
public sealed class ApiFactory(int permitPerMinute = 1000) : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";
    public const string CatalogReaderKey = "test-catalog-reader-key";
    public const string OtherShopKey = "test-other-shop-key";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"erpsync-tests-{Guid.NewGuid():N}.db");

    public FakeErpClient Erp { get; } = new();

    public async Task SyncAsync(SyncEntity entity)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISyncRunner>().RunAsync(entity, "test", TestContext.Current.CancellationToken);
    }

    public async Task DispatchOrdersAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOrderDispatcher>().DispatchDueOrdersAsync(TestContext.Current.CancellationToken);
    }

    public HttpClient CreateClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ErpSync", $"Data Source={_databasePath}");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("RateLimiting:PermitPerMinute", permitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Erp:BaseUrl", "http://erp.invalid");
        builder.UseSetting("Erp:ClientId", "test");
        builder.UseSetting("Erp:ClientSecret", "test");

        builder.UseSetting("Authentication:ApiClients:0:Name", "test-admin");
        builder.UseSetting("Authentication:ApiClients:0:KeyHash", Hash(AdminKey));
        builder.UseSetting("Authentication:ApiClients:0:Scopes:0", "catalog:read");
        builder.UseSetting("Authentication:ApiClients:0:Scopes:1", "orders:write");
        builder.UseSetting("Authentication:ApiClients:0:Scopes:2", "sync:admin");

        builder.UseSetting("Authentication:ApiClients:1:Name", "test-catalog-reader");
        builder.UseSetting("Authentication:ApiClients:1:KeyHash", Hash(CatalogReaderKey));
        builder.UseSetting("Authentication:ApiClients:1:Scopes:0", "catalog:read");

        builder.UseSetting("Authentication:ApiClients:2:Name", "test-other-shop");
        builder.UseSetting("Authentication:ApiClients:2:KeyHash", Hash(OtherShopKey));
        builder.UseSetting("Authentication:ApiClients:2:Scopes:0", "catalog:read");
        builder.UseSetting("Authentication:ApiClients:2:Scopes:1", "orders:write");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IErpClient>();
            services.AddSingleton<IErpClient>(Erp);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
