using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErpSync.Application.Abstractions.Erp;
using ErpSync.Domain.Sync;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ErpSync.Infrastructure.Erp;

/// <summary>
/// HTTP adapter for the ERP REST API. Besides mapping the wire format, it turns transport failures
/// into the two outcomes the application understands: "rejected" (don't retry) and "unavailable"
/// (retry), including whether a failed call may still have been processed.
/// </summary>
internal sealed partial class ErpApiClient(HttpClient http, ILogger<ErpApiClient> logger) : IErpClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ErpPage<ErpProduct>> GetProductsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        GetFeedPageAsync<ErpProduct>("api/v1/products", after, limit, cancellationToken);

    public Task<ErpPage<ErpStockLevel>> GetStockLevelsAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        GetFeedPageAsync<ErpStockLevel>("api/v1/stock-levels", after, limit, cancellationToken);

    public Task<ErpPage<ErpCustomer>> GetCustomersAsync(SyncPosition? after, int limit, CancellationToken cancellationToken) =>
        GetFeedPageAsync<ErpCustomer>("api/v1/customers", after, limit, cancellationToken);

    public async Task<ErpOrderConfirmation> CreateSalesOrderAsync(ErpSalesOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/sales-orders")
        {
            Content = JsonContent.Create(order, options: Json),
        };

        // Lets the ERP de-duplicate if the same order is ever sent twice.
        request.Headers.Add("Idempotency-Key", order.ExternalReference);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, isUnsafe: true, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ErpOrderConfirmation>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ErpOrderConfirmation?> FindSalesOrderAsync(string externalReference, CancellationToken cancellationToken)
    {
        var uri = $"api/v1/sales-orders?externalReference={Uri.EscapeDataString(externalReference)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, isUnsafe: false, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ErpOrderConfirmation>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ErpPage<T>> GetFeedPageAsync<T>(string path, SyncPosition? after, int limit, CancellationToken cancellationToken)
        where T : IErpRecord
    {
        var query = $"limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (after is not null)
        {
            query += $"&afterModifiedAt={Uri.EscapeDataString(after.ModifiedAt.ToString("O", CultureInfo.InvariantCulture))}";
            if (after.Key.Length > 0)
            {
                query += $"&afterKey={Uri.EscapeDataString(after.Key)}";
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?{query}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, isUnsafe: false, cancellationToken).ConfigureAwait(false);

        var page = await ReadAsync<WirePage<T>>(response, cancellationToken).ConfigureAwait(false);
        return new ErpPage<T>(page.Items ?? [], page.HasMore);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isUnsafe = request.Method != HttpMethod.Get;
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            // The circuit is open: nothing was sent.
            throw new ErpUnavailableException("The ERP circuit breaker is open after repeated failures.", outcomeUnknown: false, ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new ErpUnavailableException("The ERP did not answer in time.", outcomeUnknown: isUnsafe, ex);
        }
        catch (HttpRequestException ex)
        {
            // A connection failure means the request never reached the ERP.
            var mayHaveArrived = isUnsafe && ex.HttpRequestError != HttpRequestError.ConnectionError
                                          && ex.HttpRequestError != HttpRequestError.NameResolutionError;
            // The inner exception (with host details) is logged by the caller; the message stays neutral
            // because it can end up on the order that API clients read.
            throw new ErpUnavailableException($"Could not call the ERP ({ex.HttpRequestError}).", mayHaveArrived, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ErpUnavailableException("The ERP call timed out.", outcomeUnknown: isUnsafe, ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, bool isUnsafe, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var detail = await ReadProblemDetailAsync(response, cancellationToken).ConfigureAwait(false);
        LogErpError(logger, status, response.RequestMessage?.Method.Method, response.RequestMessage?.RequestUri?.AbsolutePath, detail);

        switch (response.StatusCode)
        {
            // Many ERPs answer 409 when an order with the same key/reference already exists.
            // That is not a rejection: treat it as ambiguous so the next attempt reconciles.
            case HttpStatusCode.Conflict when isUnsafe:
                throw new ErpUnavailableException($"The ERP reported a conflict (HTTP {status}): {detail}", outcomeUnknown: true);

            case HttpStatusCode.BadRequest:
            case HttpStatusCode.NotFound:
            case HttpStatusCode.Conflict:
            case HttpStatusCode.UnprocessableEntity:
                throw new ErpRejectedException($"The ERP rejected the request (HTTP {status}): {detail}");

            // The ERP explicitly did not process these.
            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new ErpUnavailableException($"The ERP is not accepting requests (HTTP {status}): {detail}", outcomeUnknown: false);

            // 500, 502, 504 and friends: the ERP may have done the work before failing.
            default:
                throw new ErpUnavailableException($"The ERP failed (HTTP {status}): {detail}", outcomeUnknown: isUnsafe);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new ErpUnavailableException("The ERP returned an empty response.", outcomeUnknown: false);
        }
        catch (JsonException ex)
        {
            throw new ErpUnavailableException("The ERP returned a response in an unexpected format.", outcomeUnknown: false, ex);
        }
    }

    private static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<WireProblem>(Json, cancellationToken).ConfigureAwait(false);
            return problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? "no details";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return response.ReasonPhrase ?? "no details";
        }
    }

    private sealed record WirePage<T>(IReadOnlyList<T>? Items, bool HasMore);

    private sealed record WireProblem(string? Title, string? Detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ERP returned HTTP {Status} for {Method} {Path}: {Detail}")]
    private static partial void LogErpError(ILogger logger, int status, string? method, string? path, string detail);
}
