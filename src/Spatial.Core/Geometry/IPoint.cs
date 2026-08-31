namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="Point"/>: the single-coordinate simple-feature
/// geometry. Bound by adapters and codecs that only inspect or transport a
/// point without depending on its concrete type (ADR-0029 contract faces).
/// </summary>
public interface IPoint : IGeometry
{
    /// <summary>The coordinate, or <c>null</c> when the point is empty.</summary>
    Coordinate? Coordinate { get; }

    /// <summary>X ordinate, or <c>null</c> when the point is empty.</summary>
    double? X { get; }

    /// <summary>Y ordinate, or <c>null</c> when the point is empty.</summary>
    double? Y { get; }

    /// <summary>Z ordinate, or <c>null</c> when absent.</summary>
    double? Z { get; }

    /// <summary>M ordinate, or <c>null</c> when absent.</summary>
    double? M { get; }
}
