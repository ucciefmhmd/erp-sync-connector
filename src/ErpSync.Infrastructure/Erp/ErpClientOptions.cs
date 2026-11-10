using System.ComponentModel.DataAnnotations;

namespace ErpSync.Infrastructure.Erp;

public sealed class ErpClientOptions
{
    public const string SectionName = "Erp";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Connector's client id at the ERP. Supply via user secrets or environment variables.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Connector's client secret at the ERP. Never commit it to source control.</summary>
    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Timeout for a single HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for a call including its retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Retries for safe (GET) calls. POST calls are never retried here; the order outbox handles them.</summary>
    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;
}
