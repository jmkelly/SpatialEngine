namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="MultiPoint"/>: a collection of points. Bound
/// by adapters and codecs that only inspect or transport the collection
/// without depending on its concrete type (ADR-0029 contract faces).
/// </summary>
public interface IMultiPoint : IGeometry
{
    /// <summary>The member points, in order.</summary>
    IReadOnlyList<Point> Points { get; }
}
