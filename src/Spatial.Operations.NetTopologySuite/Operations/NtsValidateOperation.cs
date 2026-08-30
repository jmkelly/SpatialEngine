using NetTopologySuite.Operation.Valid;
using Spatial.Operations.NetTopologySuite.Adapters;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The <c>spatial.geometry.validate@1</c> runner: reports whether a geometry
/// is structurally valid per the OGC simple-feature rules. Ring rules NTS's
/// LinearRing factory cannot even represent (open or undersized rings) are
/// checked on the core geometry first (<see cref="GeometryValidity"/>); the
/// remaining structural validity question is answered by NTS's
/// <c>IsValidOp</c>. An invalid geometry is a successful <c>false</c>
/// result, never a failure.
/// </summary>
internal static class NtsValidateOperation
{
    public static ValueTask<CapabilityResult> RunAsync(CapabilityInvocation invocation)
    {
        if (!OperationArguments.TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            if (!GeometryValidity.RingRulesHold(geometry!))
            {
                return Success(false);
            }

            var isValid = new IsValidOp(GeometryAdapter.ToNts(geometry!)).IsValid;
            return Success(isValid);
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
