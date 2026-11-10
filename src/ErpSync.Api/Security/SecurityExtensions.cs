using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ErpSync.Api.Security;

internal static class SecurityExtensions
{
    public const string ApiRateLimitPolicy = "api";

    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var clients = ReadApiClients(configuration);
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, options =>
            {
                foreach (var client in clients)
                {
                    options.Clients.Add(client);
                }
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Scopes.CatalogRead, policy => policy.RequireClaim(Scopes.ClaimType, Scopes.CatalogRead))
            .AddPolicy(Scopes.OrdersWrite, policy => policy.RequireClaim(Scopes.ClaimType, Scopes.OrdersWrite))
            .AddPolicy(Scopes.SyncAdmin, policy => policy.RequireClaim(Scopes.ClaimType, Scopes.SyncAdmin));

        var permitPerMinute = configuration.GetValue("RateLimiting:PermitPerMinute", 120);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // One budget per API client; unauthenticated callers share a budget per IP address.
            options.AddPolicy(ApiRateLimitPolicy, context =>
            {
                var partition = context.User.FindFirstValue(ClaimTypes.Name)
                    ?? $"ip:{context.Connection.RemoteIpAddress}";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests" },
                }).ConfigureAwait(false);
            };
        });

        return services;
    }

    /// <summary>
    /// Reads and validates the configured API clients at startup. A typo (empty name, malformed hash,
    /// misspelled scope) stops the app instead of silently weakening authentication.
    /// </summary>
    private static List<ApiClient> ReadApiClients(IConfiguration configuration)
    {
        var clients = new List<ApiClient>();
        foreach (var section in configuration.GetSection("Authentication:ApiClients").GetChildren())
        {
            var client = new ApiClient
            {
                Name = section["Name"]?.Trim() ?? string.Empty,
                KeyHash = section["KeyHash"]?.Trim() ?? string.Empty,
            };

            foreach (var scope in section.GetSection("Scopes").GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(scope.Value))
                {
                    client.Scopes.Add(scope.Value.Trim());
                }
            }

            var where = $"Authentication:ApiClients:{section.Key}";
            if (client.Name.Length == 0)
            {
                throw new InvalidOperationException($"{where}: Name is required.");
            }

            if (client.KeyHash.Length != 64 || !client.KeyHash.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"{where}: KeyHash must be a 64-character hex SHA-256 hash.");
            }

            if (client.Scopes.FirstOrDefault(s => !Scopes.All.Contains(s)) is { } unknown)
            {
                throw new InvalidOperationException($"{where}: unknown scope '{unknown}'. Valid scopes: {string.Join(", ", Scopes.All)}.");
            }

            if (clients.Any(c => string.Equals(c.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{where}: duplicate client name '{client.Name}'.");
            }

            clients.Add(client);
        }

        return clients;
    }

    /// <summary>Conservative headers for a JSON API that is never rendered in a browser frame.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";

                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    headers.CacheControl = "no-store";
                    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
                }

                return Task.CompletedTask;
            });

            await next(context).ConfigureAwait(false);
        });
}
