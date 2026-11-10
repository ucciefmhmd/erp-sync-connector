using ErpSync.Api.Security;
using ErpSync.Application.Abstractions.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ErpSync.Api.Endpoints;

internal static class CatalogEndpoints
{
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        var catalog = api.MapGroup(string.Empty)
            .WithTags("Catalog")
            .RequireAuthorization(Scopes.CatalogRead);

        catalog.MapGet("/products", ListProductsAsync)
            .WithName("ListProducts")
            .WithSummary("Lists synced products, newest data from the ERP's point of view.");

        catalog.MapGet("/products/{sku}", GetProductAsync)
            .WithName("GetProduct")
            .WithSummary("Gets a product with its stock per warehouse.");

        catalog.MapGet("/customers/{code}", GetCustomerAsync)
            .WithName("GetCustomer")
            .WithSummary("Gets a customer and its open balance.");

        return api;
    }

    private static async Task<Results<Ok<PagedResult<ProductDto>>, ValidationProblem>> ListProductsAsync(
        IReadModelQueries queries,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50,
        bool? isActive = null,
        DateTimeOffset? modifiedSince = null,
        string? search = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (page < 1)
        {
            errors["page"] = ["Must be 1 or greater."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {MaxPageSize}."];
        }

        if (search is { Length: > 100 })
        {
            errors["search"] = ["Must be at most 100 characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await queries.ListProductsAsync(
            new ProductListQuery(page, pageSize, isActive, modifiedSince, search), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ProductDetailsDto>, NotFound>> GetProductAsync(
        string sku, IReadModelQueries queries, CancellationToken cancellationToken) =>
        await queries.GetProductAsync(sku, cancellationToken).ConfigureAwait(false) is { } product
            ? TypedResults.Ok(product)
            : TypedResults.NotFound();

    private static async Task<Results<Ok<CustomerDto>, NotFound>> GetCustomerAsync(
        string code, IReadModelQueries queries, CancellationToken cancellationToken) =>
        await queries.GetCustomerAsync(code, cancellationToken).ConfigureAwait(false) is { } customer
            ? TypedResults.Ok(customer)
            : TypedResults.NotFound();
}
