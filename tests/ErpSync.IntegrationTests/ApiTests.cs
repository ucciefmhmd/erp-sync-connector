using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Sync;

namespace ErpSync.IntegrationTests;

public class ApiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Liveness_probe_is_public()
    {
        await using var api = new ApiFactory();

        var response = await api.CreateClient().GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_api_key_gets_401_problem_details()
    {
        await using var api = new ApiFactory();

        var response = await api.CreateClient().GetAsync(new Uri("/api/v1/products", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ApiKey", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_api_key_is_rejected()
    {
        await using var api = new ApiFactory();

        var response = await api.CreateClient("not-a-real-key").GetAsync(new Uri("/api/v1/products", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Key_without_the_required_scope_gets_403()
    {
        await using var api = new ApiFactory();
        var reader = api.CreateClient(ApiFactory.CatalogReaderKey);

        var response = await PostOrderAsync(reader, "key-1", "C-001", ("SKU-001", 1m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Synced_products_are_served_with_paging_and_search()
    {
        await using var api = new ApiFactory();
        await SeedAsync(api);
        var client = api.CreateClient(ApiFactory.CatalogReaderKey);

        using var page = await GetJsonAsync(client, "/api/v1/products?pageSize=2&page=1");
        using var search = await GetJsonAsync(client, "/api/v1/products?search=pump");
        using var details = await GetJsonAsync(client, "/api/v1/products/SKU-001");

        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("SKU-002", search.RootElement.GetProperty("items")[0].GetProperty("sku").GetString());
        Assert.Equal(15m, details.RootElement.GetProperty("totalOnHand").GetDecimal());
    }

    [Fact]
    public async Task Invalid_query_parameters_get_400()
    {
        await using var api = new ApiFactory();

        var response = await api.CreateClient(ApiFactory.AdminKey)
            .GetAsync(new Uri("/api/v1/products?pageSize=5000", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Order_lifecycle_accept_replay_then_deliver()
    {
        await using var api = new ApiFactory();
        await SeedAsync(api);
        var client = api.CreateClient(ApiFactory.AdminKey);

        var accepted = await PostOrderAsync(client, "order-123", "C-001", ("SKU-001", 2m));
        var replay = await PostOrderAsync(client, "order-123", "C-001", ("SKU-001", 2.0m));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var location = accepted.Headers.Location!;
        using (var pending = await ReadJsonAsync(accepted))
        using (var replayed = await ReadJsonAsync(replay))
        {
            Assert.Equal("pending", pending.RootElement.GetProperty("status").GetString());
            Assert.Equal(pending.RootElement.GetProperty("id").GetGuid(), replayed.RootElement.GetProperty("id").GetGuid());
        }

        await api.DispatchOrdersAsync();

        using var delivered = await GetJsonAsync(client, location.ToString());
        Assert.Equal("submitted", delivered.RootElement.GetProperty("status").GetString());
        Assert.Equal("SO-0001", delivered.RootElement.GetProperty("erpOrderNumber").GetString());
        Assert.Single(api.Erp.Orders);
    }

    [Fact]
    public async Task Order_requires_an_idempotency_key()
    {
        await using var api = new ApiFactory();

        var response = await PostOrderAsync(api.CreateClient(ApiFactory.AdminKey), idempotencyKey: null, "C-001", ("SKU-001", 1m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadJsonAsync(response);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task Same_key_with_a_different_order_gets_409()
    {
        await using var api = new ApiFactory();
        await SeedAsync(api);
        var client = api.CreateClient(ApiFactory.AdminKey);
        await PostOrderAsync(client, "order-1", "C-001", ("SKU-001", 1m));

        var response = await PostOrderAsync(client, "order-1", "C-001", ("SKU-002", 7m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Order_for_unknown_records_gets_422_with_field_errors()
    {
        await using var api = new ApiFactory();
        await SeedAsync(api);

        var response = await PostOrderAsync(api.CreateClient(ApiFactory.AdminKey), "order-1", "C-999", ("SKU-001", 1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = await ReadJsonAsync(response);
        Assert.Equal("catalog_mismatch", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("customerCode", out _));
    }

    [Fact]
    public async Task Malformed_json_gets_400_without_internal_details()
    {
        await using var api = new ApiFactory();
        using var content = new StringContent("{\"customerCode\":", System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales-orders") { Content = content };
        request.Headers.Add("Idempotency-Key", "k1");

        var response = await api.CreateClient(ApiFactory.AdminKey).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers_and_a_trace_id()
    {
        await using var api = new ApiFactory();

        var response = await api.CreateClient(ApiFactory.AdminKey).GetAsync(new Uri("/api/v1/products", UriKind.Relative), Ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(string.IsNullOrEmpty(response.Headers.GetValues("X-Trace-Id").Single()));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Sync_can_be_requested_through_the_api()
    {
        await using var api = new ApiFactory();
        var client = api.CreateClient(ApiFactory.AdminKey);

        var accepted = await client.PostAsync(new Uri("/api/v1/sync/stock/run", UriKind.Relative), null, Ct);
        var unknown = await client.PostAsync(new Uri("/api/v1/sync/invoices/run", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Clients_cannot_see_or_replay_each_others_orders()
    {
        await using var api = new ApiFactory();
        await SeedAsync(api);
        var shopA = api.CreateClient(ApiFactory.AdminKey);
        var shopB = api.CreateClient(ApiFactory.OtherShopKey);

        var fromA = await PostOrderAsync(shopA, "order-1", "C-001", ("SKU-001", 1m));
        var peek = await shopB.GetAsync(fromA.Headers.Location!, Ct);
        var sameKeyFromB = await PostOrderAsync(shopB, "order-1", "C-001", ("SKU-001", 1m));

        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, sameKeyFromB.StatusCode); // B's own new order, not A's
        Assert.NotEqual(fromA.Headers.Location, sameKeyFromB.Headers.Location);
    }

    [Fact]
    public async Task Unauthenticated_callers_are_rate_limited_too()
    {
        await using var api = new ApiFactory(permitPerMinute: 2);
        var anonymous = api.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync(new Uri("/api/v1/products", UriKind.Relative), Ct)).StatusCode);
        }

        var limited = await anonymous.GetAsync(new Uri("/api/v1/products", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Clients_over_their_rate_limit_get_429()
    {
        await using var api = new ApiFactory(permitPerMinute: 2);
        var client = api.CreateClient(ApiFactory.AdminKey);

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync(new Uri("/api/v1/sync/checkpoints", UriKind.Relative), Ct)).EnsureSuccessStatusCode();
        }

        var limited = await client.GetAsync(new Uri("/api/v1/sync/checkpoints", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static async Task SeedAsync(ApiFactory api)
    {
        api.Erp.Products.AddRange(
        [
            new ErpProduct("SKU-001", "Steel Bracket", 12.5m, "EUR", true, T0.AddMinutes(1)),
            new ErpProduct("SKU-002", "Water Pump", 220m, "EUR", true, T0.AddMinutes(2)),
            new ErpProduct("SKU-003", "Old Valve", 8m, "EUR", false, T0.AddMinutes(3)),
        ]);
        api.Erp.StockLevels.AddRange(
        [
            new ErpStockLevel("SKU-001", "CAI-01", 10m, T0.AddMinutes(1)),
            new ErpStockLevel("SKU-001", "RUH-01", 5m, T0.AddMinutes(2)),
        ]);
        api.Erp.Customers.Add(new ErpCustomer("C-001", "Acme Trading", "ap@acme.example", 0m, "EUR", true, T0));

        await api.SyncAsync(SyncEntity.Products);
        await api.SyncAsync(SyncEntity.Stock);
        await api.SyncAsync(SyncEntity.Customers);
    }

    private static async Task<HttpResponseMessage> PostOrderAsync(
        HttpClient client, string? idempotencyKey, string customerCode, params (string Sku, decimal Quantity)[] lines)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales-orders")
        {
            Content = JsonContent.Create(new
            {
                customerCode,
                customerReference = "PO-42",
                lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }),
            }),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
}
