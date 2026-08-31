using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>
/// Maps <c>/api/plugins</c> (plan §12, Epic F): the loaded plugin worker
/// packages and their lifecycle — the surface the workbench's runtime-status
/// screen and the replacement demonstration drive. The supervisor's workers
/// carry the state; the host merely projects it into contract shapes.
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
    }

    /// <summary>Every activated plugin worker package, ordered by provider id (empty when none are configured).</summary>
    internal static Ok<PluginDto[]> List(SpatialHostRuntime host)
    {
        var plugins = (host.Supervisor?.Workers ?? [])
            .OrderBy(worker => worker.ProviderId)
            .Select(ApiMappers.ToPluginDto)
            .ToArray();
        return TypedResults.Ok(plugins);
    }

    /// <summary>One plugin worker package by provider id (for example <c>nts@1</c>), or 404.</summary>
    internal static Results<Ok<PluginDto>, NotFound> Get(string id, SpatialHostRuntime host)
    {
        if (!ProviderId.TryParse(id, out var providerId)
            || host.Supervisor?.Workers.FirstOrDefault(worker => worker.ProviderId == providerId) is not { } worker)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(ApiMappers.ToPluginDto(worker));
    }
}