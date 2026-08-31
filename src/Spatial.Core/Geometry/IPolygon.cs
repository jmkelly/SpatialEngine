namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="Polygon"/>: an exterior ring plus interior
/// rings. Bound by adapters and codecs that only inspect or transport a
/// polygon without depending on its concrete type (ADR-0029 contract faces).
/// </summary>
public interface IPolygon : IGeometry
{
    /// <summary>The exterior ring; empty when the polygon is empty.</summary>
    LineString ExteriorRing { get; }

    /// <summary>The interior rings, in ring order.</summary>
    IReadOnlyList<LineString> InteriorRings { get; }
}
