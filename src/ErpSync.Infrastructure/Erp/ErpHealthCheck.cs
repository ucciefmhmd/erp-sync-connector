using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ErpSync.Infrastructure.Erp;

/// <summary>
/// Reports whether the ERP is reachable. When it isn't, the connector is degraded rather than
/// down: it still serves synced data and accepts orders into the outbox.
/// </summary>
internal sealed class ErpHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public const string HttpClientName = "erp-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(new Uri("health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("ERP reachable.")
                : HealthCheckResult.Degraded($"ERP health endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("ERP unreachable.", ex);
        }
    }
}
