using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Evaluates Feature Service queries (spec §9.1.4): matches the supported
/// query subset against the store, then shapes the matched set into the
/// response. Split out of <see cref="FeatureService"/> so the protocol facade
/// keeps only its per-operation surface; the Feature (object) resource, the
/// ordering, the paging, the projection and the response writers are separate
/// collaborators (ADR-0040).
/// </summary>
internal static class FeatureQueryEngine
{
    /// <summary>Executes a query and writes the spec §9.1.4.3 response.</summary>
    public static async Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var queryGeometry = FeatureProjection.TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = await FeatureSpatialMatcher.MatchAsync(new FeatureSpatialMatcher.QuerySpec(dataset, store, query, queryGeometry, operations, scheme), cancellationToken);
        return Project(dataset, matches, query, layerCrs, transforms, cancellationToken);
    }

    /// <summary>
    /// Shapes a matched feature set into the spec §9.1.4.3 response: ordering,
    /// the mutually exclusive result shapes, paging, <c>outSR</c> reprojection
    /// and the Esri JSON. Shared by the Feature/Map query path and the Image
    /// Service catalog query (spec §8.0.5, ADR-0051), whose catalog is a
    /// feature-like table of raster items.
    /// </summary>
    internal static IResult Project(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var ordered = FeatureOrdering.Apply([.. matches], FeatureOrdering.Compile(dataset, query));
        if (query.ReturnIdsOnly)
        {
            return FeatureResponseWriter.IdsOnly(ordered);
        }

        if (query.ReturnCountOnly)
        {
            return FeatureResponseWriter.CountResponse(dataset, ordered, query);
        }

        if (query.ReturnExtentOnly)
        {
            return FeatureResponseWriter.ExtentOnly(ordered, layerCrs, query.OutSr, transforms, cancellationToken);
        }

        if (query.ReturnDistinctValues)
        {
            return FeatureResponseWriter.DistinctValues(dataset, ordered, query, layerCrs);
        }

        if (query.OutStatistics is not null)
        {
            return FeatureStatisticsEngine.Statistics(dataset, ordered, query);
        }

        if (query.ReturnUniqueIdsOnly)
        {
            return FeatureResponseWriter.UniqueIdsOnly(dataset, ordered);
        }

        var page = FeaturePaging.Page(ordered, query);
        var features = page.Items
            .Select(item => FeatureProjection.TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return FeatureResponseWriter.WriteFeatures(dataset, layerCrs, query, features, page.Exceeded, page.NextToken);
    }
}
