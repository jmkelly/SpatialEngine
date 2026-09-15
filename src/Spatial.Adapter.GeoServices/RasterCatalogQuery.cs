using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Evaluates the Image Service catalog <c>query</c> (spec §8.0.5, ADR-0051).
/// An Image Server's raster catalog is a feature-like table of raster items,
/// so the query reuses the Feature Service engine's safe <c>where</c> subset,
/// ordering, paging, result shapes and <c>outSR</c> projection; only the
/// match source differs (the provider's in-memory catalog items instead of a
/// feature store). No protocol type enters the SDK.
/// </summary>
internal static class RasterCatalogQuery
{
    /// <summary>Matches and shapes a catalog query into the spec §8.0.5 response.</summary>
    public static IResult Query(
        RasterDatasetDescription description,
        IReadOnlyList<RasterCatalogItem> items,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var dataset = Describe(description, items.Count);
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var queryGeometry = FeatureQueryEngine.TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = Match(dataset, description, items, query, queryGeometry, operations, cancellationToken);
        return FeatureQueryEngine.Project(dataset, matches, query, layerCrs, transforms, cancellationToken);
    }

    private static List<FeatureQueryEngine.MatchedFeature> Match(
        DatasetDescription dataset,
        RasterDatasetDescription description,
        IReadOnlyList<RasterCatalogItem> items,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        var schema = description.CatalogSchema ?? throw GeoServicesErrors.Invalid(
            $"Image Service '{description.Dataset}' does not include an accessible raster catalog.");
        var matches = new List<FeatureQueryEngine.MatchedFeature>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = ImageService.Feature(item, schema);
            var uniqueId = EsriUniqueIdScheme.ResolveFor(query, dataset, feature);
            if (FeatureSpatialMatcher.Matches(new FeatureSpatialMatcher.MatchCandidate(query, feature, item.ObjectId, queryGeometry, operations, uniqueId), cancellationToken))
            {
                matches.Add(new FeatureQueryEngine.MatchedFeature(item.ObjectId, feature));
            }
        }

        return matches;
    }

    /// <summary>
    /// The catalog as a <see cref="DatasetDescription"/> so the shared query
    /// engine can shape it: identity, polygon footprint, the catalog schema
    /// and the raster CRS. The schema keeps its own <c>OBJECTID</c> field, so
    /// only the geometry type and CRS are inferred.
    /// </summary>
    private static DatasetDescription Describe(RasterDatasetDescription description, int itemCount)
    {
        var schema = description.CatalogSchema ?? new FeatureSchema([]);
        return new DatasetDescription(
            description.Dataset,
            description.Dataset,
            description.Name,
            GeometryColumn(schema),
            MapServerResources.SridOf(description.Raster.Crs),
            "polygon",
            itemCount,
            [description.ObjectIdField ?? "OBJECTID"],
            schema);
    }

    /// <summary>The catalog's footprint column: the schema's first geometry field, else the conventional name.</summary>
    private static string GeometryColumn(FeatureSchema schema)
    {
        foreach (var field in schema.Fields)
        {
            if (field.Kind == AttributeKind.Geometry)
            {
                return field.Name;
            }
        }

        return "Shape";
    }
}
