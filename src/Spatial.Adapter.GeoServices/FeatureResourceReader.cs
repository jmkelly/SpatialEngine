using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature (object) resource (spec §9.1.2): reads one feature by its Esri
/// <c>OBJECTID</c> and writes the <c>{"feature": ...}</c> envelope. Split out
/// of <see cref="FeatureQueryEngine"/> so the query path and the single-feature
/// read carry their own fan-out (ADR-0040).
/// </summary>
internal static class FeatureResourceReader
{
    /// <summary>
    /// The object id is resolved exactly as <c>query</c> does — the identity
    /// column when the layer has one, otherwise the scan ordinal — so the
    /// resource agrees with <c>returnIdsOnly</c>.
    /// </summary>
    public static async Task<IResult> FeatureAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        long objectId,
        EsriFeatureQuery query,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var candidate))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (candidate != objectId)
            {
                continue;
            }

            var transformed = FeatureProjection.TransformFeature(new MatchedFeature(objectId, feature), query, layerCrs, transforms, cancellationToken);
            return FeatureResponseWriter.WriteFeature(transformed, query);
        }

        throw GeoServicesErrors.NotFound($"Feature {objectId} does not exist in layer '{dataset.Id}'.");
    }
}
