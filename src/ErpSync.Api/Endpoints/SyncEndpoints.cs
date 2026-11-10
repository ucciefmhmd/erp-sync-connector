using ErpSync.Api.Security;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Sync;
using ErpSync.Domain.Sync;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ErpSync.Api.Endpoints;

internal static class SyncEndpoints
{
    public static RouteGroupBuilder MapSyncEndpoints(this RouteGroupBuilder api)
    {
        var sync = api.MapGroup("/sync")
            .WithTags("Sync administration")
            .RequireAuthorization(Scopes.SyncAdmin);

        sync.MapPost("/{entity}/run", RequestRun)
            .WithName("RequestSyncRun")
            .WithSummary("Asks the background worker to sync an entity type now (products, stock or customers).");

        sync.MapGet("/runs", ListRunsAsync)
            .WithName("ListSyncRuns")
            .WithSummary("Recent sync runs, newest first.");

        sync.MapGet("/checkpoints", ListCheckpointsAsync)
            .WithName("ListSyncCheckpoints")
            .WithSummary("How far each ERP change feed has been read.");

        return api;
    }

    private static Results<Accepted, ProblemHttpResult> RequestRun(string entity, SyncRequestChannel requests)
    {
        if (!TryParseEntity(entity, out var syncEntity))
        {
            return TypedResults.Problem(
                $"Unknown entity '{entity}'. Use products, stock or customers.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        return requests.TryRequest(syncEntity)
            ? TypedResults.Accepted("/api/v1/sync/runs")
            : TypedResults.Problem("Too many sync requests are queued; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Ok<IReadOnlyList<SyncRunDto>>, ValidationProblem>> ListRunsAsync(
        IReadModelQueries queries, CancellationToken cancellationToken, string? entity = null, int take = 20)
    {
        SyncEntity? filter = null;
        if (entity is not null)
        {
            if (!TryParseEntity(entity, out var parsed))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["entity"] = ["Use products, stock or customers."] });
            }

            filter = parsed;
        }

        if (take is < 1 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["take"] = ["Must be between 1 and 100."] });
        }

        return TypedResults.Ok(await queries.ListSyncRunsAsync(filter, take, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Ok<IReadOnlyList<SyncCheckpointDto>>> ListCheckpointsAsync(
        IReadModelQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.ListCheckpointsAsync(cancellationToken).ConfigureAwait(false));

    private static bool TryParseEntity(string value, out SyncEntity entity) =>
        Enum.TryParse(value, ignoreCase: true, out entity) && Enum.IsDefined(entity);
}
