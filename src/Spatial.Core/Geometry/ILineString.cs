namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="LineString"/>: a single ordered coordinate
/// sequence. Bound by adapters and codecs that only inspect or transport a
/// line without depending on its concrete type (ADR-0029 contract faces).
/// </summary>
public interface ILineString : IGeometry
{
    /// <summary>The ordered coordinates of the line.</summary>
    ICoordinateSequence Sequence { get; }

    /// <summary>The coordinate at <paramref name="index"/>.</summary>
    Coordinate this[int index] { get; }
}
