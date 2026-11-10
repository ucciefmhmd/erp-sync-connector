using System.Net;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Infrastructure.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ErpSync.UnitTests.Erp;

public class ErpAuthenticationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Token_is_reused_until_shortly_before_it_expires()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var tokenEndpoint = IssuingTokens(expiresInSeconds: 600);
        using var tokens = Provider(tokenEndpoint, clock);

        var first = await tokens.GetTokenAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(8)); // 2 minutes left: still usable
        var second = await tokens.GetTokenAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(90)); // inside the 60-second refresh margin
        var third = await tokens.GetTokenAsync(Ct);

        Assert.Equal("token-1", first);
        Assert.Equal("token-1", second);
        Assert.Equal("token-2", third);
        Assert.Equal(2, tokenEndpoint.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_token_request()
    {
        var tokenEndpoint = IssuingTokens(expiresInSeconds: 600);
        using var tokens = Provider(tokenEndpoint, TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tokens.GetTokenAsync(Ct).AsTask()));

        Assert.All(results, token => Assert.Equal("token-1", token));
        Assert.Single(tokenEndpoint.Requests);
    }

    [Fact]
    public async Task Rejected_credentials_surface_as_unavailable_without_a_known_outcome_problem()
    {
        var tokenEndpoint = StubHandler.Json(HttpStatusCode.Unauthorized, """{"title":"Invalid client credentials."}""");
        using var tokens = Provider(tokenEndpoint, TimeProvider.System);

        var error = await Assert.ThrowsAsync<ErpUnavailableException>(() => tokens.GetTokenAsync(Ct).AsTask());

        Assert.False(error.OutcomeUnknown); // nothing was sent to the ERP yet
        Assert.Contains("credentials", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_retries_once()
    {
        var tokenEndpoint = IssuingTokens(expiresInSeconds: 600);
        using var tokens = Provider(tokenEndpoint, TimeProvider.System);

        // The ERP revoked token-1 early: the first call is refused, the retry with token-2 succeeds.
        var erp = new StubHandler((request, _) => request.Headers.Authorization?.Parameter == "token-1"
            ? StubHandler.Response(HttpStatusCode.Unauthorized, "{}")
            : StubHandler.Response(HttpStatusCode.OK, """{"items":[],"hasMore":false}"""));

        using var client = new HttpClient(new ErpAuthenticationHandler(tokens) { InnerHandler = erp })
        {
            BaseAddress = new Uri("https://erp.test/"),
        };

        using var response = await client.GetAsync(new Uri("api/v1/products?limit=1", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["token-1", "token-2"], erp.Requests.Select(r => r.BearerToken));
        Assert.Equal(2, tokenEndpoint.Requests.Count);
    }

    [Fact]
    public async Task Persistent_401_is_returned_after_one_retry_instead_of_looping()
    {
        var tokenEndpoint = IssuingTokens(expiresInSeconds: 600);
        using var tokens = Provider(tokenEndpoint, TimeProvider.System);
        var erp = StubHandler.Json(HttpStatusCode.Unauthorized, "{}");

        using var client = new HttpClient(new ErpAuthenticationHandler(tokens) { InnerHandler = erp })
        {
            BaseAddress = new Uri("https://erp.test/"),
        };

        using var response = await client.GetAsync(new Uri("api/v1/products?limit=1", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, erp.Requests.Count);
    }

    private static StubHandler IssuingTokens(int expiresInSeconds) =>
        new((_, call) => StubHandler.Response(
            HttpStatusCode.OK, $$"""{"accessToken":"token-{{call}}","expiresIn":{{expiresInSeconds}}}"""));

    private static ErpTokenProvider Provider(HttpMessageHandler tokenEndpoint, TimeProvider clock) =>
        new(
            new StubHttpClientFactory(tokenEndpoint),
            Options.Create(new ErpClientOptions { BaseUrl = "https://erp.test", ClientId = "id", ClientSecret = "secret" }),
            clock,
            NullLogger<ErpTokenProvider>.Instance);
}
