using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// The measurement verbs of the geometry service (ADR-0036): area, length,
/// distance and label points. Split from <see cref="IGeometryOperations"/> so
/// an implementation can advertise measurement without claiming the set and
/// construction verbs. Pure, planar and cancellable.
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
}
