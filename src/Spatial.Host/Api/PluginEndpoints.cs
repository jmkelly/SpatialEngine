using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>
/// Maps <c>/api/plugins</c> (plan §12, Epic F): the loaded plugin worker
/// packages and their lifecycle — the surface the workbench's runtime-status
/// screen and the replacement demonstration drive. The supervisor's workers
/// carry the state; the host merely projects it into contract shapes. The
/// POST control surface (ADR-0031) turns <c>RouteNewWorkTo</c>,
/// <c>DrainAsync</c> and <c>RollbackAsync</c> into HTTP so the browser
/// workbench can demonstrate side-by-side replacement (plan §17.9-11)
/// without stopping the host.
/// </summary>
internal static class PluginEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plugins", List)
            .Produces<IReadOnlyList<PluginDto>>();

        app.MapGet("/api/plugins/{id}", Get)
            .Produces<PluginDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/plugins/{id}/route-new-work", RouteNewWork)
            .Produces<PluginDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost("/api/plugins/{id}/drain", Drain)
            .Produces<PluginDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/plugins/{id}/rollback", Rollback)
            .Produces<PluginDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>Every activated plugin worker package, ordered by provider id (empty when none are configured).</summary>
    internal static Ok<PluginDto[]> List(SpatialHostRuntime host)
    {
        var plugins = (host.Supervisor?.Workers ?? [])
            .OrderBy(worker => worker.ProviderId)
            .Select(PluginApiMappers.ToPluginDto)
            .ToArray();
        return TypedResults.Ok(plugins);
    }

    /// <summary>One plugin worker package by provider id (for example <c>nts@1</c>), or 404.</summary>
    internal static Results<Ok<PluginDto>, NotFound> Get(string id, SpatialHostRuntime host)
    {
        if (TryFind(host, id) is not { } worker)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(PluginApiMappers.ToPluginDto(worker));
    }

    /// <summary>
    /// Routes new work to the provider for every capability it serves
    /// (active-preference routing, plan §10.4) — the soft, reversible switch
    /// the replacement demonstration drives. A worker that is not serving
    /// right now (draining/stopped/failed) is a conflict: routing to it would
    /// look successful and route nothing.
    /// </summary>
    internal static Results<Ok<PluginDto>, NotFound, Conflict> RouteNewWork(string id, SpatialHostRuntime host)
    {
        var supervisor = host.Supervisor;
        if (supervisor is null || TryFind(host, id) is not { } worker)
        {
            return TypedResults.NotFound();
        }

        if (!WorkerStateSupport.CanServe(worker.State))
        {
            return TypedResults.Conflict();
        }

        supervisor.RouteNewWorkTo(worker.ProviderId);
        return TypedResults.Ok(PluginApiMappers.ToPluginDto(worker));
    }

    /// <summary>
    /// Drains the worker (plan §10.4): stops routing new work, waits for
    /// in-flight invocations, reclaims resources and stops the process. The
    /// host keeps running; the drained version can be reactivated any time.
    /// Idempotent — a terminal worker answers with its current state.
    /// </summary>
    internal static async Task<Results<Ok<PluginDto>, NotFound>> Drain(string id, SpatialHostRuntime host)
    {
        var supervisor = host.Supervisor;
        if (supervisor is null || TryFind(host, id) is not { } worker)
        {
            return TypedResults.NotFound();
        }

        await supervisor.DrainAsync(worker.Id);
        return TypedResults.Ok(PluginApiMappers.ToPluginDto(worker));
    }

    /// <summary>
    /// Rolls new work back to the provider: reactivates its package when the
    /// worker is stopped or failed and routes new work to it (plan §10.4
    /// rollback) — the reverse of the replacement demonstration.
    /// </summary>
    internal static async Task<Results<Ok<PluginDto>, NotFound, Conflict<string>>> Rollback(string id, SpatialHostRuntime host)
    {
        var supervisor = host.Supervisor;
        if (supervisor is null || TryFind(host, id) is not { } worker)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var rolledBack = await supervisor.RollbackAsync(worker.Package.Path);
            return TypedResults.Ok(PluginApiMappers.ToPluginDto(rolledBack));
        }
        catch (WorkerActivationException exception)
        {
            return TypedResults.Conflict(exception.Message);
        }
    }

    private static WorkerInstance? TryFind(SpatialHostRuntime host, string id) =>
        ProviderId.TryParse(id, out var providerId)
            ? host.Supervisor?.Workers.FirstOrDefault(worker => worker.ProviderId == providerId)
            : null;
}
