using System.Diagnostics.CodeAnalysis;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Valid;
using NetTopologySuite.Simplify;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using NtsTopologyException = NetTopologySuite.Geometries.TopologyException;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The runners for the four geometry operation contracts, plus the pieces
/// they share: argument parsing (shape and range problems are
/// <c>invalid.arguments</c> naming the argument, ADR-0026), the OGC ring
/// rules the validation contract pre-checks on the core geometry (an open or
/// undersized ring cannot even be represented as an NTS LinearRing) and the
/// uniform failure mapping (an NTS <see cref="TopologyException"/> or
/// <see cref="ArgumentException"/> is an input the algorithm cannot process;
/// anything else is a provider failure). All four operations are synchronous
/// planar NTS computations — cancellation is honoured before the algorithm
/// runs (NTS itself has no cancellation hooks).
/// </summary>
internal static class NtsOperationRunner
{
    public static ValueTask<CapabilityResult> BufferAsync(CapabilityInvocation invocation)
    {
        if (!TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error)
            || !TryFiniteDouble(invocation, GeometryOperationArguments.Distance, out var distance, out error)
            || !TryOptionalPositiveInt(invocation, GeometryOperationArguments.QuadrantSegments, 8, out var quadrantSegments, out error))
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
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    public static ValueTask<CapabilityResult> IntersectionAsync(CapabilityInvocation invocation)
    {
        if (!TryGeometry(invocation, GeometryOperationArguments.Left, out var left, out var error)
            || !TryGeometry(invocation, GeometryOperationArguments.Right, out var right, out error))
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
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    public static ValueTask<CapabilityResult> ValidateAsync(CapabilityInvocation invocation)
    {
        if (!TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            if (!RingsAreValid(geometry!))
            {
                return Success(false);
            }

            return Success(new IsValidOp(GeometryAdapter.ToNts(geometry!)).IsValid);
        }
        catch (Exception exception)
        {
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    public static ValueTask<CapabilityResult> SimplifyAsync(CapabilityInvocation invocation)
    {
        if (!TryGeometry(invocation, GeometryOperationArguments.Geometry, out var geometry, out var error)
            || !TryFiniteDouble(invocation, GeometryOperationArguments.Tolerance, out var tolerance, out error))
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
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    /// <summary>Reads the named geometry argument; a missing or mistyped value is an input-contract violation.</summary>
    private static bool TryGeometry(
        CapabilityInvocation invocation,
        string name,
        [NotNullWhen(true)] out IGeometry? geometry,
        out CapabilityError? error)
    {
        geometry = null;
        error = null;
        if (invocation.TryGetArgument<IGeometry>(name, out var value))
        {
            geometry = value;
            return true;
        }

        error = CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires '{name}' to carry a spatial geometry (canonical binary interchange).");
        return false;
    }

    /// <summary>Reads the named double argument, accepting every numeric wire form that is finite.</summary>
    private static bool TryFiniteDouble(
        CapabilityInvocation invocation,
        string name,
        out double value,
        out CapabilityError? error)
    {
        value = 0;
        error = null;
        if (!TryNumber(invocation, name, out var raw, out error))
        {
            return false;
        }

        if (!double.IsFinite(raw))
        {
            error = CapabilityError.InvalidArguments($"'{name}' must be a finite number, got {raw}.");
            return false;
        }

        value = raw;
        return true;
    }

    /// <summary>Reads an optional positive integer argument with a fallback when absent; accepts int32 and int64 wire forms.</summary>
    private static bool TryOptionalPositiveInt(
        CapabilityInvocation invocation,
        string name,
        int fallback,
        out int value,
        out CapabilityError? error)
    {
        value = fallback;
        error = null;
        if (!invocation.Arguments.TryGetValue(name, out var raw))
        {
            return true;
        }

        var parsed = raw switch
        {
            int number => (long)number,
            long number => number,
            _ => -1L,
        };
        if (parsed < 1)
        {
            error = CapabilityError.InvalidArguments(
                $"'{name}', when provided, must be a positive int32, got {(raw is null ? "nothing" : $"'{raw.GetType().Name}'")}.");
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static bool TryNumber(
        CapabilityInvocation invocation,
        string name,
        out double value,
        out CapabilityError? error)
    {
        value = 0;
        error = null;
        if (!invocation.Arguments.TryGetValue(name, out var raw) || raw is null)
        {
            error = CapabilityError.InvalidArguments(
                $"{invocation.Capability} requires '{name}' to be a number, got nothing.");
            return false;
        }

        if (TryConvertToDouble(raw, out value))
        {
            return true;
        }

        error = CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires '{name}' to be a number, got '{raw.GetType().Name}'.");
        return false;
    }

    private static bool TryConvertToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double number:
                value = number;
                return true;
            case float number:
                value = number;
                return true;
            case int number:
                value = number;
                return true;
            case long number:
                value = number;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// The OGC ring rules checked before asking NetTopologySuite, because an
    /// open or undersized ring cannot even be constructed as an NTS
    /// LinearRing: every polygon ring must be closed and carry at least four
    /// coordinates when non-empty. Only polygon rings are rings — standalone
    /// line strings are never treated as rings.
    /// </summary>
    private static bool RingsAreValid(IGeometry geometry)
    {
        foreach (var part in geometry.DepthFirst())
        {
            if (part is IPolygon polygon && !PolygonRingsAreValid(polygon))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PolygonRingsAreValid(IPolygon polygon)
    {
        if (!RingIsValid(polygon.ExteriorRing))
        {
            return false;
        }

        foreach (var hole in polygon.InteriorRings)
        {
            if (!RingIsValid(hole))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RingIsValid(LineString ring)
    {
        var sequence = ring.Sequence;
        if (sequence.Count == 0)
        {
            return true;
        }

        if (sequence.Count < 4)
        {
            return false;
        }

        return sequence.GetCoordinate(0) == sequence.GetCoordinate(sequence.Count - 1);
    }

    /// <summary>Uniform failure mapping (ADR-0026): unprocessable inputs are invalid.arguments, everything else is a provider failure.</summary>
    private static CapabilityError MapFailure(CapabilityId capability, Exception exception)
    {
        if (exception is NtsTopologyException or ArgumentException or FormatException)
        {
            return CapabilityError.InvalidArguments(
                $"{capability} could not process the input geometry: {exception.Message}");
        }

        return CapabilityError.ProviderFailure(
            $"{capability} failed unexpectedly while running the NetTopologySuite algorithm: {exception.Message}");
    }

    private static ValueTask<CapabilityResult> Fail(CapabilityError error) =>
        new(CapabilityResult.Failure(error));

    private static ValueTask<CapabilityResult> Success(object value) =>
        new(CapabilityResult.Success(value));
}
