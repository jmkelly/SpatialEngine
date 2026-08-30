using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The <c>spatial.geometry.buffer@1</c> runner: expands or shrinks a geometry
/// by a finite distance (negative distances erode), honouring an optional
/// quadrant segment count. The buffer is a planar NTS computation, so the
/// result carries the input's CRS identity and an XY layout; the operation is
/// synchronous and fast — cancellation is honoured before the algorithm runs.
/// </summary>
internal static class NtsBufferOperation
{
    public static ValueTask<CapabilityResult> RunAsync(CapabilityInvocation invocation)
    {
        if (!OperationArguments.TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error)
            || !OperationArguments.TryFiniteDouble(invocation, GeometryOperationArguments.Distance, out var distance, out error)
            || !OperationArguments.TryOptionalPositiveInt(
                invocation, GeometryOperationArguments.QuadrantSegments, 8, out var quadrantSegments, out error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            var buffered = GeometryAdapter.ToNts(geometry!).Buffer(distance, quadrantSegments);
            return Success(GeometryAdapter.ToCore(buffered, geometry!.CoordinateReference));
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