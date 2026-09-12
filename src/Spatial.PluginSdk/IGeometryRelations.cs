using Spatial.Core.Geometry;

namespace Spatial.PluginSdk;

/// <summary>
/// The spatial-relation verbs of the geometry service (ADR-0036): DE-9IM
/// intersection-pattern tests used by the GeoServices <c>relation</c>
/// operation and the Feature Service <c>spatialRel</c> family. Pure and
/// cancellable.
/// </summary>
public interface IGeometryRelations
{
    /// <summary>
    /// Whether the two geometries satisfy the DE-9IM intersection pattern
    /// (for example <c>T*T***T**</c>), per the OGC relate definition.
    /// </summary>
    bool Relate(IGeometry left, IGeometry right, string intersectionPattern, CancellationToken cancellationToken = default);
}
