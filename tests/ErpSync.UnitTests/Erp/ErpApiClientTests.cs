using System.Net;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Sync;
using ErpSync.Infrastructure.Erp;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpSync.UnitTests.Erp;

/// <summary>
/// The adapter's main job besides mapping JSON: decide whether a failure means "rejected" (don't retry),
/// "unavailable, nothing happened" (retry) or "unavailable, may have happened" (reconcile first).
/// </summary>
public class ErpApiClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ErpSalesOrder Order = new("ref-123", "C-001", "PO-1", [new ErpSalesOrderLine("SKU-1", 2m)]);

    [Fact]
    public async Task Feed_request_carries_the_keyset_position_and_the_page_is_mapped()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK,
            """{"items":[{"sku":"SKU-2","name":"Valve","unitPrice":12.5,"currency":"EUR","isActive":true,"modifiedAt":"2026-01-01T08:05:00+00:00"}],"hasMore":true}""");

        var page = await Client(stub).GetProductsAsync(new SyncPosition(T0, "SKU-1"), 50, Ct);

        var query = Uri.UnescapeDataString(stub.Requests.Single().Uri.Query);
        Assert.Contains("limit=50", query, StringComparison.Ordinal);
        Assert.Contains("afterModifiedAt=2026-01-01T08:00:00.0000000+00:00", query, StringComparison.Ordinal);
        Assert.Contains("afterKey=SKU-1", query, StringComparison.Ordinal);
        Assert.True(page.HasMore);
        Assert.Equal("Valve", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Overlap_start_position_sends_no_key()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, """{"items":[],"hasMore":false}""");

        await Client(stub).GetStockLevelsAsync(new SyncPosition(T0, string.Empty), 10, Ct);

        Assert.DoesNotContain("afterKey", stub.Requests.Single().Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_order_sends_the_external_reference_as_idempotency_key()
    {
        var stub = StubHandler.Json(HttpStatusCode.Created, """{"orderNumber":"SO-000042"}""");

        var confirmation = await Client(stub).CreateSalesOrderAsync(Order, Ct);

        var request = stub.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("ref-123", request.IdempotencyKey);
        Assert.Contains("\"customerCode\":\"C-001\"", request.Body, StringComparison.Ordinal);
        Assert.Equal("SO-000042", confirmation.OrderNumber);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Client_errors_on_create_are_rejections(HttpStatusCode status)
    {
        var stub = StubHandler.Json(status, """{"title":"Order rejected.","detail":"Customer C-001 is blocked."}""");

        var error = await Assert.ThrowsAsync<ErpRejectedException>(() => Client(stub).CreateSalesOrderAsync(Order, Ct));

        Assert.Contains("Customer C-001 is blocked.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)] // explicitly not processed
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)] // may have been processed before failing
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.Conflict, true)] // "already exists" on many ERPs: reconcile, don't reject
    public async Task Server_side_failures_on_create_are_classified_by_whether_the_order_may_exist(HttpStatusCode status, bool outcomeUnknown)
    {
        var stub = StubHandler.Json(status, """{"title":"failure"}""");

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(stub).CreateSalesOrderAsync(Order, Ct));

        Assert.Equal(outcomeUnknown, error.OutcomeUnknown);
    }

    [Fact]
    public async Task A_failing_read_never_marks_the_outcome_as_unknown()
    {
        var stub = StubHandler.Json(HttpStatusCode.InternalServerError, "{}");

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(stub).GetCustomersAsync(null, 10, Ct));

        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task Connection_failure_means_the_order_was_never_sent()
    {
        var handler = new ThrowingHandler(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(handler).CreateSalesOrderAsync(Order, Ct));

        Assert.False(error.OutcomeUnknown);
        Assert.DoesNotContain("connection refused", error.Message, StringComparison.Ordinal); // no transport details leak
    }

    [Fact]
    public async Task Connection_dropped_mid_response_means_the_order_may_exist()
    {
        var handler = new ThrowingHandler(new HttpRequestException(HttpRequestError.ResponseEnded, "response ended"));

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(handler).CreateSalesOrderAsync(Order, Ct));

        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task Timeout_on_create_means_the_order_may_exist()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("timed out"));

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(handler).CreateSalesOrderAsync(Order, Ct));

        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task Find_returns_null_when_the_erp_has_no_such_order()
    {
        var stub = StubHandler.Json(HttpStatusCode.NotFound, "{}");

        var found = await Client(stub).FindSalesOrderAsync("ref-123", Ct);

        Assert.Null(found);
        Assert.Contains("externalReference=ref-123", stub.Requests.Single().Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_response_is_reported_as_unavailable_not_as_a_crash()
    {
        var stub = new StubHandler((_, _) => StubHandler.Response(HttpStatusCode.OK, "<html>maintenance</html>", "text/html"));

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => Client(stub).GetProductsAsync(null, 10, Ct));

        Assert.False(error.OutcomeUnknown);
    }

    private static ErpApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://erp.test/") }, NullLogger<ErpApiClient>.Instance);
}
