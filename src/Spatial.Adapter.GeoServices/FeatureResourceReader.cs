using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature (object) resource (spec §9.1.2): reads one feature by its Esri
/// <c>OBJECTID</c> and writes the <c>{"feature": ...}</c> envelope. Split out
/// of <see cref="FeatureQueryEngine"/> so the query path and the single-feature
/// read carry their own fan-out (ADR-0040). The object id is resolved through
/// <see cref="FeatureAttachmentTargets"/>, so the resource reads one feature
/// through the store's identity lookup rather than scanning the layer
/// (ADR-0038, ADR-0112).
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
        QueryServices services,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var feature = await FeatureAttachmentTargets
            .FindFeatureAsync(dataset, store, objectId, cancellationToken)
            .ConfigureAwait(false);
        var transformed = FeatureProjection.TransformFeature(
            new MatchedFeature(objectId, feature), query, layerCrs, services.Transforms, services.Operations, cancellationToken);
        return FeatureResponseWriter.WriteFeature(transformed, query, services);
    }
}
