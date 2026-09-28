using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// The measurement verbs of the geometry service (ADR-0036): area, length,
/// distance, label points and centroids. Split from
/// <see cref="IGeometryOperations"/> so an implementation can advertise
/// measurement without claiming the set and construction verbs. Pure,
/// planar and cancellable.
/// </summary>
public interface IGeometryMeasures
{
    /// <summary>The planar area of a polygonal geometry (0 for non-areal shapes).</summary>
    double Area(IGeometry geometry, CancellationToken cancellationToken = default);

    /// <summary>The planar length of a linear geometry (0 for non-linear shapes).</summary>
    double Length(IGeometry geometry, CancellationToken cancellationToken = default);

    /// <summary>The planar distance between two geometries.</summary>
    double Distance(IGeometry left, IGeometry right, CancellationToken cancellationToken = default);

    /// <summary>A point guaranteed to lie on or inside the geometry (an interior point).</summary>
    IGeometry LabelPoint(IGeometry geometry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The geometry's centre of mass: the area centroid of a polygon, the
    /// length midpoint of a line, the point itself for a point, and the
    /// merged centroid of a collection. Distinct from
    /// <see cref="LabelPoint"/>, which is guaranteed to lie *inside* the
    /// geometry, and from the envelope middle, which is structural and
    /// wrong for any concave or asymmetric shape. An empty geometry
    /// yields an empty point.
    /// </summary>
    IGeometry Centroid(IGeometry geometry, CancellationToken cancellationToken = default);
}
