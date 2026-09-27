using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// Buffering by a ground distance in metres whatever the geometry's
/// coordinate reference system (ADR-0075). The GeoServices <c>buffer</c>
/// operation's <c>geodesic</c> option and a linear <c>unit</c> against a
/// geographic CRS both need this; the planar
/// <see cref="IGeometryOperations.Buffer"/> only understands the units of the
/// CRS it is handed.
///
/// The contract is a capability, not a claim of exactness: implementations
/// reach the ground distance by reprojecting onto an extent-sized working
/// plane and buffering there, which is within a stated tolerance of the true
/// geodesic (0.05% relative, ADR-0075). A caller needing exactness, or a
/// working extent beyond the stated limit, gets
/// <see cref="SpatialException"/> with code <c>invalid.arguments</c>.
///
/// Pure and cancellable, over core geometry values only (ADR-0005); the
/// result carries the input's CRS.
/// </summary>
public interface IGeodesicBuffering
{
    /// <summary>
    /// Buffers <paramref name="geometry"/> by <paramref name="distanceMetres"/>
    /// measured on the ground, returning the result in the geometry's own CRS.
    /// A negative distance erodes. The geometry must carry a geographic CRS.
    /// </summary>
    IGeometry Buffer(
        IGeometry geometry,
        double distanceMetres,
        int quadrantSegments = 8,
        CancellationToken cancellationToken = default);
}
