using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Valid;
using NetTopologySuite.Simplify;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite geometry operations (ADR-0033): a direct,
/// in-process implementation of <see cref="IGeometryOperations"/>.
/// NTS types stay inside this assembly (ADR-0005). All five operations are
/// synchronous planar computations; cancellation is honoured before the
/// algorithm runs. Invalid inputs throw <see cref="SpatialException"/> with
/// code <c>invalid.arguments</c>; an invalid geometry is a successful
/// <c>false</c>, never a failure.
/// </summary>
public sealed class NtsGeometryOperations : IGeometryOperations
{
    public IGeometry Buffer(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(distance))
        {
            throw SpatialException.BadArguments($"'distance' must be a finite number, got {distance}.");
        }

        if (quadrantSegments < 1)
        {
            throw SpatialException.BadArguments($"'quadrantSegments' must be positive, got {quadrantSegments}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var buffered = GeometryAdapter.ToNts(geometry).Buffer(distance, quadrantSegments);
            return GeometryAdapter.ToCore(buffered, geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "buffer");
        }
    }

    public IGeometry Intersection(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = OverlayNGRobust.Overlay(
                GeometryAdapter.ToNts(left),
                GeometryAdapter.ToNts(right),
                SpatialFunction.Intersection);
            return GeometryAdapter.ToCore(result, left.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "intersection");
        }
    }

    public bool Validate(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!RingsAreValid(geometry))
            {
                return false;
            }

            return new IsValidOp(GeometryAdapter.ToNts(geometry)).IsValid;
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "validate");
        }
    }

    public IGeometry Simplify(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        RequireDisplacement(tolerance, "tolerance");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var simplified = DouglasPeuckerSimplifier.Simplify(GeometryAdapter.ToNts(geometry), tolerance);
            return GeometryAdapter.ToCore(simplified, geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "simplify");
        }
    }

    /// <summary>
    /// Douglas-Peucker at the caller's deviation allowance, guarded so a
    /// feature never changes geometry kind. The guard is what separates this
    /// verb from <see cref="Simplify"/> (ADR-0079): at an allowance wider than
    /// the geometry, DP empties a small ring, and a query response may not
    /// answer a polygon request with nothing at all — so the input is
    /// returned unchanged instead.
    /// </summary>
    public IGeometry Generalize(IGeometry geometry, double maxDisplacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        RequireDisplacement(maxDisplacement, "maxDisplacement");
        cancellationToken.ThrowIfCancellationRequested();
        if (maxDisplacement == 0)
        {
            // A zero allowance cannot spend any deviation, and Douglas-Peucker
            // still rewrites the ring closure (it re-appends the first
            // coordinate), so the only compliant answer is the input itself.
            return geometry;
        }

        try
        {
            var source = GeometryAdapter.ToNts(geometry);
            var generalized = DouglasPeuckerSimplifier.Simplify(source, maxDisplacement);
            return generalized.IsEmpty || generalized.GeometryType != source.GeometryType
                ? geometry
                : GeometryAdapter.ToCore(generalized, geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "generalize");
        }
    }

    private static void RequireDisplacement(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw SpatialException.BadArguments($"'{name}' must be a finite number, got {value}.");
        }

        if (value < 0)
        {
            throw SpatialException.BadArguments($"'{name}' must be non-negative, got {value}.");
        }
    }

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
}
