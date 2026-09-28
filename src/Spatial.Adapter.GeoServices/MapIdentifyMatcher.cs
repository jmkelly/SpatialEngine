using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer identify match pipeline (spec §4.0.5): scan each selected
/// layer's features, keep the ones that pass the layer's attribute and
/// temporal filters and whose geometry intersects the query geometry
/// (reprojected into the layer's own CRS first), and project the hit's
/// geometry back to the identify CRS. Split out of
/// <see cref="MapIdentifyEngine"/> so matching is testable and named apart
/// from the request grammar that plans it.
/// </summary>
internal static class MapIdentifyMatcher
{
    /// <summary>Every hit across the selected layers, layer by layer.</summary>
    public static async Task<List<IdentifyHit>> MatchAsync(
        IdentifyServices services,
        IReadOnlyList<MapLayerInfo> layers,
        IdentifyQuery query,
        CancellationToken cancellationToken)
    {
        var hits = new List<IdentifyHit>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CollectLayerHitsAsync(services, layer, query, hits, cancellationToken);
        }

        return hits;
    }

    /// <summary>Scans one layer's features, keeping the hits that pass the layer's filters and intersect the query.</summary>
    private static async Task CollectLayerHitsAsync(
        IdentifyServices services,
        MapLayerInfo layer,
        IdentifyQuery query,
        List<IdentifyHit> hits,
        CancellationToken cancellationToken)
    {
        // The clause was compiled to the engine's predicate vocabulary when
        // the layerDefs parameter parsed, so there is no text left to parse.
        var definition = query.LayerDefs?.GetValueOrDefault(layer.Layer.Id);
        var scheme = definition is null ? null : EsriObjectIdScheme.For(layer.Dataset);
        var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Dataset.Srid);
        var localQuery = Transform(query.QueryGeometry, layerCrs, services.Transforms, cancellationToken);
        var extent = query.Times?.GetValueOrDefault(layer.Layer.Id);
        var batches = await services.Store.ScanAsync(layer.Layer.Dataset, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!IdentifyFilters.MatchesFilters(scheme, layer.Dataset, definition, feature, ordinal, extent))
            {
                continue;
            }

            if (FeatureGeometry.Find(feature) is not { } geometry
                || services.Operations.Intersection(geometry, localQuery, cancellationToken).IsEmpty)
            {
                continue;
            }

            var projected = query.ReturnGeometry ? Transform(geometry, query.IdentifyCrs, services.Transforms, cancellationToken) : null;
            hits.Add(new IdentifyHit(layer, feature, projected));
        }
    }

    private static IGeometry Transform(IGeometry geometry, CoordinateReference? target, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
        target is { } to && geometry.CoordinateReference is { } from && from != to
            ? transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken)
            : geometry;
}
