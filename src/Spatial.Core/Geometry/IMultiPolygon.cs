namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="MultiPolygon"/>: a collection of polygons.
/// Bound by adapters and codecs that only inspect or transport the
/// collection without depending on its concrete type (ADR-0029 contract
/// faces).
/// </summary>
public interface IMultiPolygon : IGeometry
{
    /// <summary>The member polygons, in order.</summary>
    IReadOnlyList<Polygon> Polygons { get; }
}
