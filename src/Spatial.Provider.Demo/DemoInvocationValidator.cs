using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo;

/// <summary>
/// The store-free argument-validation surface of the demo provider: the
/// guards the handlers run before touching the in-memory catalog. No member
/// here mutates anything, so the whole surface is unit-testable in isolation.
/// </summary>
internal static class DemoInvocationValidator
{
    /// <summary>Returns the first non-null error of the guard list, or null when every guard passed.</summary>
    internal static CapabilityError? FirstError(params CapabilityError?[] errors)
    {
        foreach (var error in errors)
        {
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>A pre-cancelled invocation fails with a cancellable error before touching the catalog.</summary>
    internal static CapabilityError? CheckCancelled(CapabilityInvocation invocation) =>
        invocation.CancellationToken.IsCancellationRequested
            ? CapabilityError.Cancelled(invocation.Capability)
            : null;

    /// <summary>An invocation without runtime facilities cannot mint streams.</summary>
    internal static CapabilityError? RequireFacilities(CapabilityInvocation invocation, out ICapabilityFacilities facilities)
    {
        if (invocation.Facilities is { } present)
        {
            facilities = present;
            return null;
        }

        facilities = null!;
        return CapabilityError.InvalidArguments(
            $"{invocation.Capability} needs the runtime stream facilities to answer.");
    }

    /// <summary>Reads the optional <c>pattern</c> argument (a LIKE-style filter).</summary>
    internal static CapabilityError? ReadPattern(CapabilityInvocation invocation, out string? pattern)
    {
        pattern = invocation.TryGetArgument<string>("pattern", out var requested) ? requested : null;
        return pattern is not null && pattern.Length == 0
            ? CapabilityError.InvalidArguments("the 'pattern' must be non-empty when provided.")
            : null;
    }

    /// <summary>Reads the <c>dataset</c> identifier — required for describe, scan and query.</summary>
    internal static CapabilityError? ReadDataset(CapabilityInvocation invocation, out string dataset)
    {
        if (invocation.TryGetArgument<string>("dataset", out var text) && text.Length > 0)
        {
            dataset = text;
            return null;
        }

        dataset = string.Empty;
        return CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires a 'dataset' identifier (schema.table or table).");
    }

    /// <summary>Reads an all-or-none bounding box for feature.query.</summary>
    internal static CapabilityError? ReadBoundingBox(
        CapabilityInvocation invocation,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        minX = minY = maxX = maxY = 0;
        var hasMinX = invocation.TryGetArgument<double>("minx", out minX);
        var hasMinY = invocation.TryGetArgument<double>("miny", out minY);
        var hasMaxX = invocation.TryGetArgument<double>("maxx", out maxX);
        var hasMaxY = invocation.TryGetArgument<double>("maxy", out maxY);
        var any = hasMinX || hasMinY || hasMaxX || hasMaxY;
        if (!any)
        {
            return null;
        }

        return hasMinX && hasMinY && hasMaxX && hasMaxY && minX <= maxX && minY <= maxY
            ? null
            : CapabilityError.InvalidArguments(
                "feature.query accepts an all-or-none bounding box: 'minx','miny','maxx','maxy' numeric bounds with minx<=maxx and miny<=maxy.");
    }

    /// <summary>Rejects attribute filters: the demo query supports bounding boxes only.</summary>
    internal static CapabilityError? RejectFilter(CapabilityInvocation invocation)
    {
        return invocation.Arguments.ContainsKey("filter")
            ? CapabilityError.InvalidArguments(
                "demo@1's feature.query supports bounding-box filtering only; the 'filter' expression is not supported by the demo provider.")
            : null;
    }
}
