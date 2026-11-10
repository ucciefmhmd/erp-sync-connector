using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErpSync.Api.Endpoints;
using ErpSync.Api.Errors;
using ErpSync.Api.OpenApi;
using ErpSync.Api.Security;
using ErpSync.Api.Workers;
using ErpSync.Application;
using ErpSync.Infrastructure;
using ErpSync.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    // Readable lines locally; structured JSON (one event per line) for log shipping everywhere else.
    if (builder.Environment.IsDevelopment())
    {
        logger.WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {TraceId}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture);
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 256 * 1024;
});

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>());

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity(builder.Configuration);

var workers = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
if (workers.Enabled)
{
    builder.Services.AddHostedService<SyncWorker>();
    builder.Services.AddHostedService<OrderDispatchWorker>();
}

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.InitializeDatabaseAsync().ConfigureAwait(false);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    // TLS is expected to terminate at the ingress/reverse proxy; HSTS tells clients to stay on HTTPS.
    app.UseHsts();
}

app.UseSecurityHeaders();
app.Use(async (context, next) =>
{
    // Echo the trace id so a caller can quote it; the same id appears in every log line for the request.
    context.Response.Headers["X-Trace-Id"] = Activity.Current?.TraceId.ToString();
    await next(context).ConfigureAwait(false);
});
app.UseSerilogRequestLogging(request =>
    request.EnrichDiagnosticContext = (diagnostics, http) =>
        diagnostics.Set("ClientName", http.User.Identity?.Name ?? "anonymous"));

// The limiter runs after authentication (so it can partition by client) but before authorization,
// so unauthenticated and unauthorized calls are limited too (per IP) instead of being free to hammer.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi();
    app.MapScalarApiReference(scalar => scalar.WithTitle("ERP Sync Connector API"));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ErpSync.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapGroup("/api/v1")
    .RequireRateLimiting(SecurityExtensions.ApiRateLimitPolicy)
    .MapCatalogEndpoints()
    .MapOrderEndpoints()
    .MapSyncEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
