using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>Maps <c>/api/capabilities</c>: the registered capability contract surface (plan §9, §12).</summary>
internal static class CapabilityEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/capabilities", List)
            .Produces<IReadOnlyList<CapabilitySummaryDto>>();

        app.MapGet("/api/capabilities/{id}", Detail)
            .Produces<CapabilityDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Every registered capability, ordered by id, with its serving providers.</summary>
    internal static Ok<CapabilitySummaryDto[]> List(SpatialHostRuntime host)
    {
        var summaries = host.Runtime.Registry.Capabilities
            .Select(capability => CapabilityApiMappers.ToCapabilitySummary(host.Runtime.Registry, capability))
            .ToArray();
        return TypedResults.Ok(summaries);
    }

    /// <summary>The full declaration of one capability, or 404 when nothing serves it.</summary>
    internal static Results<Ok<CapabilityDetailDto>, NotFound> Detail(string id, SpatialHostRuntime host)
    {
        if (!CapabilityId.TryParse(id, out var capability)
            || host.Runtime.Registry.GetProviders(capability).Count == 0)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(CapabilityApiMappers.ToCapabilityDetail(host.Runtime.Registry, capability));
    }
}
