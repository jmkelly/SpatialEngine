using NetTopologySuite.Densify;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite set/construction service (ADR-0036):
/// union, difference, convex hull, densify and topological repair
/// (<c>simplify</c>-as-MakeValid). NTS types stay inside this assembly
/// (ADR-0005). Repair uses <c>GeometryFixer</c>, the NTS port of the OGC
/// MakeValid algorithm: a self-intersecting ring is split into its valid
/// parts rather than dropped.
/// </summary>
public sealed class NtsGeometryProcessing : IGeometryProcessing
{
    public IGeometry Union(IReadOnlyList<IGeometry> geometries, CancellationToken cancellationToken = default)
    {
        var (parts, crs) = Convert(geometries, "union");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToCore(UnaryUnionOp.Union(parts), crs);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "union");
        }
    }

    public IGeometry Difference(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = OverlayNGRobust.Overlay(GeometryAdapter.ToNts(left), GeometryAdapter.ToNts(right), SpatialFunction.Difference);
            return GeometryAdapter.ToCore(result, left.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "difference");
        }
    }

    public IGeometry ConvexHull(IReadOnlyList<IGeometry> geometries, CancellationToken cancellationToken = default)
    {
        var (parts, crs) = Convert(geometries, "convexHull");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToCore(GeometryCombiner.Combine(parts).ConvexHull(), crs);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "convexHull");
        }
    }

    public IGeometry Repair(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToCore(new GeometryFixer(GeometryAdapter.ToNts(geometry)).GetResult(), geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "simplify");
        }
    }

    public IGeometry Densify(IGeometry geometry, double maxSegmentLength, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(maxSegmentLength) || maxSegmentLength <= 0)
        {
            throw SpatialException.BadArguments($"'maxSegmentLength' must be a positive finite number, got {maxSegmentLength}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToCore(Densifier.Densify(GeometryAdapter.ToNts(geometry), maxSegmentLength), geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "densify");
        }
    }

    private static (NtsGeometry[] Parts, CoordinateReference? Crs) Convert(IReadOnlyList<IGeometry> geometries, string operation)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        if (geometries.Count == 0)
        {
            throw SpatialException.BadArguments($"'{operation}' needs at least one geometry.");
        }

        var parts = new NtsGeometry[geometries.Count];
        for (var i = 0; i < geometries.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(geometries[i]);
            parts[i] = GeometryAdapter.ToNts(geometries[i]);
        }

        return (parts, geometries[0].CoordinateReference);
    }
}
