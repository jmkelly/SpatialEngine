using System.Diagnostics.CodeAnalysis;

namespace Spatial.Core.Geometry;

/// <summary>
/// Contract face for heterogeneous geometry collections (and, transitively,
/// the multi-shape types that share the collection shape). Bound by adapters
/// and codecs that only inspect or transport a collection without depending
/// on its concrete type (ADR-0029 contract faces).
/// </summary>
public interface IGeometryParts : IGeometry
{
    /// <summary>The member geometries, in order.</summary>
    IReadOnlyList<IGeometry> Geometries { get; }
}
