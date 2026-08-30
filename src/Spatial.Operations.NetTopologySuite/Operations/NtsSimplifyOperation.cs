using NetTopologySuite.Simplify;
using Spatial.Operations.NetTopologySuite.Adapters;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The <c>spatial.geometry.simplify@1</c> runner: Douglas-Peucker
/// simplification at a finite, non-negative tolerance. A zero tolerance
/// returns the geometry unchanged; ordinates carried by the input layout
/// survive the simplification (the algorithm removes collinear vertices, not
/// ordinates), and the result keeps the input's CRS identity.
/// </summary>
internal static class NtsSimplifyOperation
{
    public static ValueTask<CapabilityResult> RunAsync(CapabilityInvocation invocation)
    {
        if (!OperationArguments.TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error)
            || !OperationArguments.TryFiniteDouble(invocation, GeometryOperationArguments.Tolerance, out var tolerance, out error))
        {
            return Fail(error!);
        }

        if (tolerance < 0)
        {
            return Fail(CapabilityError.InvalidArguments(
                $"'{GeometryOperationArguments.Tolerance}' must be non-negative, got {tolerance}."));
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            var simplified = DouglasPeuckerSimplifier.Simplify(GeometryAdapter.ToNts(geometry!), tolerance);
            return Success(GeometryAdapter.ToCore(simplified, geometry!.CoordinateReference));
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
