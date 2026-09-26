using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The CRS a MapServer operation falls back to when the client names none:
/// the first selected layer's own dataset CRS. Split out of the export
/// planner so the per-layer CRS resolution is named once and shared by
/// every operation that frames a request from the publication's layers.
/// </summary>
internal static class MapLayerCrs
{
    /// <summary>The first selected layer's served CRS; no layers means no CRS to fall back to.</summary>
    public static async Task<CoordinateReference?> ResolveAsync(
        IStoreRegistry stores,
        ResolvedService resolved,
        IReadOnlyList<PublishedLayer> layers,
        CancellationToken cancellationToken)
    {
        if (layers.Count == 0)
        {
            return null;
        }

        var description = await MapServerEndpoints.Catalogue(stores, resolved.Store)
            .DescribeAsync(layers[0].Dataset, cancellationToken);
        return EsriLayerModel.LayerCoordinateReference(description.Srid);
    }
}
