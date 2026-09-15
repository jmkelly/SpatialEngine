using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// The set/construction verbs of the geometry service (ADR-0036): union,
/// difference, convex hull, densify and topological repair
/// (<c>simplify</c>-as-MakeValid). Split from
/// <see cref="IGeometryOperations"/> so the adapter maps protocol verbs onto
/// a granular face. Pure, planar and cancellable.
/// </summary>
public interface IGeometryProcessing
{
    /// <summary>The union of every input geometry.</summary>
    IGeometry Union(IReadOnlyList<IGeometry> geometries, CancellationToken cancellationToken = default);

    /// <summary>The part of <paramref name="left"/> not covered by <paramref name="right"/>.</summary>
    IGeometry Difference(IGeometry left, IGeometry right, CancellationToken cancellationToken = default);

    /// <summary>The convex hull covering every input geometry.</summary>
    IGeometry ConvexHull(IReadOnlyList<IGeometry> geometries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Topological repair (the GeoServices <c>simplify</c> operation): fixes
    /// self-intersections and overlapping rings, producing a valid geometry.
    /// Deliberately distinct from <see cref="IGeometryOperations.Simplify"/>
    /// (Douglas-Peucker generalization).
    /// </summary>
    IGeometry Repair(IGeometry geometry, CancellationToken cancellationToken = default);

    /// <summary>Inserts vertices so no segment exceeds <paramref name="maxSegmentLength"/>.</summary>
    IGeometry Densify(IGeometry geometry, double maxSegmentLength, CancellationToken cancellationToken = default);
}
