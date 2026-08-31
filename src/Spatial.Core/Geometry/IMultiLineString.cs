namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for <see cref="MultiLineString"/>: a collection of line
/// strings. Bound by adapters and codecs that only inspect or transport the
/// collection without depending on its concrete type (ADR-0029 contract
/// faces).
/// </summary>
public interface IMultiLineString : IGeometry
{
    /// <summary>The member line strings, in order.</summary>
    IReadOnlyList<LineString> LineStrings { get; }
}
