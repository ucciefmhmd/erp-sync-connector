using System.Net;
using System.Net.Http.Json;
using ErpSync.Application.Abstractions.Erp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ErpSync.Infrastructure.Erp;

/// <summary>
/// Obtains and caches the ERP session token. Concurrent callers share one refresh, and the token is
/// renewed shortly before it expires.
/// </summary>
internal sealed partial class ErpTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ErpClientOptions> options,
    TimeProvider clock,
    ILogger<ErpTokenProvider> logger) : IDisposable
{
    public const string HttpClientName = "erp-auth";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile CachedToken? _cached;

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (IsUsable(_cached))
        {
            return _cached!.Value;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable(_cached))
            {
                return _cached!.Value;
            }

            _cached = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            return _cached.Value;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Drops the cached token if it is the one the ERP just rejected.</summary>
    public void Invalidate(string rejectedToken)
    {
        if (_cached is { } cached && cached.Value == rejectedToken)
        {
            _cached = null;
        }
    }

    public void Dispose() => _refreshLock.Dispose();

    private bool IsUsable(CachedToken? token) => token is not null && clock.GetUtcNow() < token.ExpiresAt - RefreshMargin;

    private async Task<CachedToken> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                "api/v1/auth/token",
                new TokenRequest(settings.ClientId, settings.ClientSecret),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or TimeoutRejectedException or BrokenCircuitException
            && !cancellationToken.IsCancellationRequested)
        {
            // The token call happens before the business request, so nothing was sent yet: outcome known.
            throw new ErpUnavailableException("Could not reach the ERP to authenticate.", outcomeUnknown: false, ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LogCredentialsRejected(logger);
                throw new ErpUnavailableException("The ERP rejected the connector's credentials.", outcomeUnknown: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ErpUnavailableException(
                    $"ERP authentication failed with HTTP {(int)response.StatusCode}.", outcomeUnknown: false);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
            if (token is null || string.IsNullOrEmpty(token.AccessToken) || token.ExpiresIn <= 0)
            {
                throw new ErpUnavailableException("The ERP returned an invalid token response.", outcomeUnknown: false);
            }

            LogTokenRefreshed(logger, token.ExpiresIn);
            return new CachedToken(token.AccessToken, clock.GetUtcNow().AddSeconds(token.ExpiresIn));
        }
    }

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    private sealed record TokenRequest(string ClientId, string ClientSecret);

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);

    [LoggerMessage(Level = LogLevel.Information, Message = "ERP access token refreshed (valid for {ExpiresIn}s)")]
    private static partial void LogTokenRefreshed(ILogger logger, int expiresIn);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The ERP rejected the connector credentials. Check Erp:ClientId and Erp:ClientSecret.")]
    private static partial void LogCredentialsRejected(ILogger logger);
}
