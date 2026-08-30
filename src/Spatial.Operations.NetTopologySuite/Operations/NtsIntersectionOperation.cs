using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The <c>spatial.geometry.intersection@1</c> runner: the set-theoretic
/// intersection of two geometries via the robust overlay engine. Callers
/// intersect geometries in the same coordinate reference; the left geometry's
/// CRS identity is carried onto the result. Disjoint inputs yield an empty
/// geometry (success), never an error.
/// </summary>
internal static class NtsIntersectionOperation
{
    public static ValueTask<CapabilityResult> RunAsync(CapabilityInvocation invocation)
    {
        if (!OperationArguments.TryGeometry(invocation, GeometryOperationArguments.Left, out var left, out var error)
            || !OperationArguments.TryGeometry(invocation, GeometryOperationArguments.Right, out var right, out error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            var intersecting = OverlayNGRobust.Overlay(
                GeometryAdapter.ToNts(left!),
                GeometryAdapter.ToNts(right!),
                SpatialFunction.Intersection);
            return Success(GeometryAdapter.ToCore(intersecting, left!.CoordinateReference));
        }
        catch (Exception exception)
        {
            return Fail(NtsFailureMapper.For(invocation.Capability, exception));
        }
    }

    private static ValueTask<CapabilityResult> Fail(CapabilityError error) =>
        new(CapabilityResult.Failure(error));

    private static ValueTask<CapabilityResult> Success(object value) =>
        new(CapabilityResult.Success(value));
}