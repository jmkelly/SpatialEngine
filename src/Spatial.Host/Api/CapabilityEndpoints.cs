using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;
using Spatial.Runtime.Capabilities;

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
        if (FindServed(id, host.Runtime.Registry) is not { } capability)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(CapabilityApiMappers.ToCapabilityDetail(host.Runtime.Registry, capability));
    }

    /// <summary>The capability id when it parses and at least one provider serves it.</summary>
    private static CapabilityId? FindServed(string id, CapabilityRegistry registry) =>
        (CapabilityId.TryParse(id, out var capability), registry.GetProviders(capability).Count == 0) switch
        {
            (true, false) => capability,
            _ => null,
        };
}
