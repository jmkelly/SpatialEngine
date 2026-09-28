using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// One feature layer resolved for an MVT tile. The feature store and catalogue
/// are core-typed service seams, and the attribute filter is the core-typed
/// predicate every store answers (ADR-0074); no provider or encoder type
/// crosses this contract (ADR-0005/ADR-0070).
/// </summary>
public sealed record VectorTileLayer(
    string Name,
    string Dataset,
    IFeatureStore Features,
    IDataCatalogue Catalogue,
    Predicate? Where = null);

/// <summary>
/// A tile in its scheme CRS. Bounds are x-first; <paramref name="Extent"/> is
/// the integer coordinate space used by the encoded geometry (normally 4096).
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
/// own scheme/cache orchestration.
/// </summary>
public interface IVectorTileService
{
    Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default);
}
