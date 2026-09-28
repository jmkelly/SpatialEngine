using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// One feature layer resolved for an MVT tile. The feature store and catalogue
/// are core-typed service seams; no provider or encoder type crosses this
/// contract (ADR-0005/ADR-0070).
/// </summary>
public sealed record VectorTileLayer(
    string Name,
    string Dataset,
    IFeatureStore Features,
    IDataCatalogue Catalogue,
    string? Filter = null);

/// <summary>
/// A tile in its scheme CRS. Bounds are x-first and must have extent on both
/// axes — a frame the integer coordinate space can address;
/// <paramref name="Extent"/> is the integer coordinate space used by the
/// encoded geometry (normally 4096).
/// </summary>
public sealed record VectorTileRequest(
    Envelope Bounds,
    string Crs,
    IReadOnlyList<VectorTileLayer> Layers,
    int Extent = 4096);

/// <summary>Encoded MVT bytes and their stable media type.</summary>
public sealed record VectorTile(byte[] Content, string MediaType = "application/vnd.mapbox-vector-tile");

/// <summary>
/// Reads resolved feature layers and encodes one MVT tile. Implementations
/// own querying, reprojection, clipping policy and protobuf encoding; callers
/// own scheme/cache orchestration. A tile is rejected as
/// <c>invalid.arguments</c> when it is not renderable — bounds without extent
/// on an axis, a blank CRS, or an extent outside 1..65536 — rather than
/// encoding geometry it cannot place.
/// </summary>
public interface IVectorTileService
{
    Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default);
}
