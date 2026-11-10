using ErpSync.Application.Abstractions.Erp;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Infrastructure.Erp;
using ErpSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ErpSync.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AddPersistence(services, configuration);
        AddErpClient(services, configuration);

        services.AddHealthChecks()
            .AddDbContextCheck<ErpSyncDbContext>("database", tags: [ReadyTag])
            .AddCheck<ErpHealthCheck>("erp", failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ErpSync") ?? "Data Source=erpsync.db";
        services.AddPersistence(options => options.UseSqlite(connectionString));
    }

    /// <summary>Registers the DbContext and repositories. Also used by tests with their own database.</summary>
    internal static IServiceCollection AddPersistence(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb)
    {
        services.AddDbContext<ErpSyncDbContext>(configureDb);

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ErpSyncDbContext>());
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<ISalesOrderRepository, SalesOrderRepository>();
        services.AddScoped<ISyncStateRepository, SyncStateRepository>();
        services.AddScoped<IReadModelQueries, ReadModelQueries>();
        return services;
    }

    private static void AddErpClient(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ErpClientOptions>()
            .Bind(configuration.GetSection(ErpClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Timeouts are needed while the pipeline is built, so read them once here.
        var settings = configuration.GetSection(ErpClientOptions.SectionName).Get<ErpClientOptions>() ?? new ErpClientOptions();

        services.AddSingleton<ErpTokenProvider>();
        services.AddTransient<ErpAuthenticationHandler>();

        services.AddHttpClient(ErpTokenProvider.HttpClientName, ConfigureBaseAddress)
            .AddStandardResilienceHandler(options => ConfigureResilience(options, settings));

        services.AddHttpClient(ErpHealthCheck.HttpClientName, ConfigureBaseAddress)
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(5));

        services.AddHttpClient<IErpClient, ErpApiClient>(ConfigureBaseAddress)
            .AddHttpMessageHandler<ErpAuthenticationHandler>()
            .AddStandardResilienceHandler(options =>
            {
                ConfigureResilience(options, settings);

                // Creating an order is not idempotent at the HTTP level, so this layer never retries
                // POSTs. The order outbox retries them, after checking whether the ERP already has the order.
                options.Retry.DisableForUnsafeHttpMethods();
            });
    }

    private static void ConfigureBaseAddress(IServiceProvider services, HttpClient http)
    {
        var options = services.GetRequiredService<IOptions<ErpClientOptions>>().Value;
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        // Polly's total timeout governs; HttpClient's own default (100s) would only get in the way.
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions options, ErpClientOptions settings)
    {
        options.Retry.MaxRetryAttempts = settings.MaxRetryAttempts;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = settings.AttemptTimeout;
        options.TotalRequestTimeout.Timeout = settings.TotalTimeout;

        // The breaker's sampling window must be at least twice the attempt timeout.
        options.CircuitBreaker.SamplingDuration = settings.AttemptTimeout * 3;
    }
}
