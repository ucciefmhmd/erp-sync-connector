using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpSync.MockErp;
using Microsoft.Extensions.Options;

// A stand-in ERP with a generic REST contract, so the connector can be run and demoed end to end.
// It deliberately misbehaves (latency, 503s, lost responses) to show the connector's resilience.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MockErpOptions>(builder.Configuration.GetSection(MockErpOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ErpStore>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddHostedService<ActivitySimulator>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }));

app.MapPost("/api/v1/auth/token", (TokenRequest request, TokenStore tokens, IOptions<MockErpOptions> options) =>
{
    var settings = options.Value;
    if (!FixedTimeEquals(request.ClientId, settings.ClientId) || !FixedTimeEquals(request.ClientSecret, settings.ClientSecret))
    {
        return Results.Problem(title: "Invalid client credentials.", statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = tokens.Issue(TimeSpan.FromSeconds(settings.TokenLifetimeSeconds));
    return Results.Ok(new { accessToken = token, expiresIn = settings.TokenLifetimeSeconds });
});

var api = app.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var tokens = http.RequestServices.GetRequiredService<TokenStore>();
    var settings = http.RequestServices.GetRequiredService<IOptions<MockErpOptions>>().Value;

    var header = http.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !tokens.IsValid(header["Bearer ".Length..]))
    {
        return Results.Problem(title: "Missing or expired token.", statusCode: StatusCodes.Status401Unauthorized);
    }

#pragma warning disable CA5394 // Random is fine for simulating faults.
    await Task.Delay(Random.Shared.Next(0, Math.Max(1, settings.MaxLatencyMs)), http.RequestAborted).ConfigureAwait(false);
    if (Random.Shared.NextDouble() < settings.FailureRate)
#pragma warning restore CA5394
    {
        http.Response.Headers.RetryAfter = "1";
        return Results.Problem(title: "ERP temporarily unavailable (simulated).", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return await next(context).ConfigureAwait(false);
});

api.MapGet("/products", (ErpStore store, int limit, DateTimeOffset? afterModifiedAt, string? afterKey) =>
    Feed(store, s => s.Products, p => p.ModifiedAt, p => p.Sku, afterModifiedAt, afterKey, limit));

api.MapGet("/stock-levels", (ErpStore store, int limit, DateTimeOffset? afterModifiedAt, string? afterKey) =>
    Feed(store, s => s.StockLevels, l => l.ModifiedAt, l => l.Key, afterModifiedAt, afterKey, limit));

api.MapGet("/customers", (ErpStore store, int limit, DateTimeOffset? afterModifiedAt, string? afterKey) =>
    Feed(store, s => s.Customers, c => c.ModifiedAt, c => c.Code, afterModifiedAt, afterKey, limit));

api.MapPost("/sales-orders", (HttpRequest http, MockOrderRequest request, ErpStore store, IOptions<MockErpOptions> options) =>
{
    var key = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.Problem(title: "Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
    }

    var (order, created, error) = store.CreateOrder(key, request);
    if (order is null)
    {
        return Results.Problem(title: "Order rejected.", detail: error, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

#pragma warning disable CA5394 // Random is fine for simulating faults.
    if (created && Random.Shared.NextDouble() < options.Value.LostOrderResponseRate)
#pragma warning restore CA5394
    {
        // The order IS saved, but the caller never learns the order number.
        return Results.Problem(title: "Internal error after saving (simulated lost response).", statusCode: StatusCodes.Status500InternalServerError);
    }

    var body = new { orderNumber = order.OrderNumber };
    return created ? Results.Created($"/api/v1/sales-orders/{order.OrderNumber}", body) : Results.Ok(body);
});

api.MapGet("/sales-orders", (string externalReference, ErpStore store) =>
    store.FindOrder(externalReference) is { } order
        ? Results.Ok(new { orderNumber = order.OrderNumber })
        : Results.NotFound());

await app.RunAsync().ConfigureAwait(false);

static IResult Feed<T>(
    ErpStore store, Func<ErpStore, IEnumerable<T>> source, Func<T, DateTimeOffset> modifiedAt, Func<T, string> key,
    DateTimeOffset? afterModifiedAt, string? afterKey, int limit)
{
    if (limit is < 1 or > 1000)
    {
        return Results.Problem(title: "limit must be between 1 and 1000.", statusCode: StatusCodes.Status400BadRequest);
    }


    // A missing afterKey means "everything from that timestamp on" (used for overlap re-reads).
    var (items, hasMore) = store.ReadFeed(source, modifiedAt, key, afterModifiedAt, afterKey ?? string.Empty, limit);
    return Results.Ok(new { items, hasMore });
}

static bool FixedTimeEquals(string? left, string right) =>
    left is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

internal sealed record TokenRequest(string ClientId, string ClientSecret);

internal sealed class TokenStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);

    public string Issue(TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = clock.GetUtcNow() + lifetime;
        return token;
    }

    public bool IsValid(string token) => _tokens.TryGetValue(token, out var expiresAt) && expiresAt > clock.GetUtcNow();
}

internal sealed partial class ActivitySimulator(ErpStore store, IOptions<MockErpOptions> options, ILogger<ActivitySimulator> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var random = new Random(7);
        using var timer = new PeriodicTimer(options.Value.MutationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            store.Mutate(random);
            LogMutated(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Simulated ERP activity (stock and price changes)")]
    private static partial void LogMutated(ILogger logger);
}
