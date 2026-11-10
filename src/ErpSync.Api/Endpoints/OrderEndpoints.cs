using System.Security.Claims;
using ErpSync.Api.Errors;
using ErpSync.Api.Security;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Orders;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ErpSync.Api.Endpoints;

public sealed record CreateSalesOrderRequest(string CustomerCode, string? CustomerReference, IReadOnlyList<CreateSalesOrderLineRequest> Lines);

public sealed record CreateSalesOrderLineRequest(string Sku, decimal Quantity);

internal static class OrderEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/sales-orders")
            .WithTags("Sales orders")
            .RequireAuthorization(Scopes.OrdersWrite);

        orders.MapPost("/", CreateAsync)
            .WithName("CreateSalesOrder")
            .WithSummary("Accepts a sales order for delivery to the ERP.")
            .WithDescription(
                "The order is stored and delivered to the ERP in the background, so the response is 202 Accepted. " +
                "Poll the Location URL for the ERP order number. Send a unique Idempotency-Key per order: " +
                "repeating a request with the same key returns the original order instead of creating a duplicate.");

        orders.MapGet("/{id:guid}", GetAsync)
            .WithName("GetSalesOrder")
            .WithSummary("Gets an order and its delivery status.");

        return api;
    }

    private static async Task<Results<Accepted<SalesOrderDto>, Ok<SalesOrderDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        CreateSalesOrderRequest request,
        ClaimsPrincipal caller,
        CreateSalesOrderHandler handler,
        IReadModelQueries queries,
        CancellationToken cancellationToken)
    {
        var clientName = ClientName(caller);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [IdempotencyHeader] = [$"The {IdempotencyHeader} header is required, for example a new GUID per order."],
            });
        }

        var command = new CreateSalesOrderCommand(
            clientName,
            idempotencyKey.Trim(),
            request.CustomerCode ?? string.Empty,
            request.CustomerReference,
            (request.Lines ?? []).Select(l => new CreateSalesOrderLine(l.Sku ?? string.Empty, l.Quantity)).ToList());

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var outcome = result.Value!;
        var order = await queries.GetSalesOrderAsync(outcome.OrderId, clientName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Order {outcome.OrderId} was saved but cannot be read back.");

        return outcome.Created
            ? TypedResults.Accepted($"/api/v1/sales-orders/{order.Id}", order)
            : TypedResults.Ok(order);
    }

    /// <summary>Another client's order id is answered with 404, so ids can't be probed.</summary>
    private static async Task<Results<Ok<SalesOrderDto>, NotFound>> GetAsync(
        Guid id, ClaimsPrincipal caller, IReadModelQueries queries, CancellationToken cancellationToken) =>
        await queries.GetSalesOrderAsync(id, ClientName(caller), cancellationToken).ConfigureAwait(false) is { } order
            ? TypedResults.Ok(order)
            : TypedResults.NotFound();

    /// <summary>The authenticated API client; set by the API-key handler, never taken from the request body.</summary>
    private static string ClientName(ClaimsPrincipal caller) =>
        caller.FindFirstValue(ClaimTypes.Name) ?? throw new InvalidOperationException("Authenticated caller has no name.");
}
