using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using static Spatial.Provider.Demo.DemoInvocationValidator;

namespace Spatial.Provider.Demo;

/// <summary>
/// The feature-data handlers of <c>demo@1</c>: feature.scan and
/// feature.query. Both run the store-free validation guards in contract
/// order, resolve the requested dataset, and hand the feature rows to
/// <see cref="DemoFeatureBatches"/> for the canonical batch stream
/// (ADR-0020/0028) — query filters by the all-or-none bounding box first.
/// Split from the runner so the dispatch surface stays thin and the box
/// semantics stay close to their validation guards.
/// </summary>
internal static class DemoFeatureHandler
{
    /// <summary>Streams all features of a dataset, unfiltered.</summary>
    public static async ValueTask<CapabilityResult> ScanAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (DemoDatasetCatalog.Find(dataset) is not { } found)
        {
            return CapabilityResult.Failure(UnknownDataset(invocation, dataset));
        }

        return await DemoFeatureBatches.EmitStreamAsync(facilities, found.Features, invocation.CancellationToken);
    }

    /// <summary>Streams the features whose envelope intersects the query box.</summary>
    public static async ValueTask<CapabilityResult> QueryAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RejectFilter(invocation),
            ReadBoundingBox(invocation, out var minX, out var minY, out var maxX, out var maxY),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (DemoDatasetCatalog.Find(dataset) is not { } found)
        {
            return CapabilityResult.Failure(UnknownDataset(invocation, dataset));
        }

        var hasBox = invocation.Arguments.ContainsKey("minx");
        var features = hasBox
            ? found.Features.Where(feature => found.BoxFor(feature.Id) is { } box
                && box.MinX <= maxX && box.MaxX >= minX
                && box.MinY <= maxY && box.MaxY >= minY)
            : found.Features;
        return await DemoFeatureBatches.EmitStreamAsync(facilities, features, invocation.CancellationToken);
    }
}
