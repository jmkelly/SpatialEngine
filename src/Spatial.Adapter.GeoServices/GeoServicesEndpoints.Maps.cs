using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;
namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Service routes (spec §4, ADR-0048/ADR-0055). This type owns the
/// mounting: the service root and layer metadata are served by
/// <see cref="MapServerMetadata"/>, the legend resources by
/// <see cref="MapServerLegendEndpoints"/>, the per-layer operations by
/// <see cref="MapServerOperationEndpoints"/>, the offline/async rejects by
/// <see cref="MapOfflineRejects"/>, and the export and vector-tile routes by
/// <see cref="MapExportEndpoints"/> and <see cref="MapVectorTileEndpoints"/>.
/// The MapServer is the GeoServices projection of a
/// <see cref="MapServiceKind.MapServer"/> publication.
/// </summary>
internal static class MapServerEndpoints
{
    internal static void MapMapServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, IEnumerable<ITileScheme> schemes, ICoordinateTransforms transforms, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            MapServerMetadata.MapServerRoot(new(catalog, registry, service, context, stores, cancellationToken), schemes, transforms, loggerFactory));
        group.MapMethods("/{service}/MapServer/layers", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerMetadata.MapAllLayers(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/MapServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerMetadata.MapLayer(new(catalog, registry, service, context, stores, cancellationToken), layerId));
        group.MapMethods("/{service}/MapServer/legend", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerLegendEndpoints.MapLegend(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/MapServer/queryDomains", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerLegendEndpoints.MapQueryDomains(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/MapServer/queryLegends", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerLegendEndpoints.MapQueryLegends(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/MapServer/{layerId:int}/generateRenderer", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerOperationEndpoints.MapGenerateRenderer(new(catalog, registry, service, context, stores, cancellationToken), layerId));
        group.MapMethods("/{service}/MapServer/{layerId:int}/images/{imageId}", ["GET", "POST"], (
            string service, int layerId, string imageId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapServerOperationEndpoints.MapImage(new(catalog, registry, service, context, stores, cancellationToken), layerId, imageId));
        group.MapMethods("/{service}/MapServer/{layerId:int}/query", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, IGeometryRelations relations, IGeometryMeasures measures,
            ICrsDirectory catalogue, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapServerOperationEndpoints.MapQuery(
                new(catalog, registry, service, context, stores, cancellationToken), layerId,
                new QueryServices(operations, relations, measures, catalogue, transforms)));
        group.MapMethods("/{service}/MapServer/identify", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapServerOperationEndpoints.MapIdentify(new(catalog, registry, service, context, stores, cancellationToken), operations, transforms));
        group.MapMethods("/{service}/MapServer/find", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapServerOperationEndpoints.MapFind(new(catalog, registry, service, context, stores, cancellationToken), transforms));
        MapOfflineEndpoints(catalog, registry, group);

        MapExportEndpoints.MapExportRoutes(group, catalog, registry);
        MapVectorTileEndpoints.MapRoutes(group, catalog, registry);
    }

    /// <summary>
    /// The offline/async reject surface (ADR-0060): <c>exportTiles</c> +
    /// <c>estimateExportTileSize</c>, WMTS (base plus the capabilities/tile
    /// remainder), KML (<c>generateKml</c> plus the <c>kml</c> image
    /// remainder) and async <c>jobs</c> (collection plus one job, its results
    /// and its inputs). Named non-goals rejected by name; T-048 builds on
    /// this scope instead of re-deciding it.
    /// </summary>
    private static void MapOfflineEndpoints(GeoServicesCatalog catalog, IMapRegistry registry, RouteGroupBuilder group)
    {
        group.MapMethods("/{service}/MapServer/exportTiles", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.MapExportTiles(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/estimateExportTileSize", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.MapEstimateExportTileSize(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/WMTS", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Wmts(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/WMTS/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Wmts(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/generateKml", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.GenerateKml(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/kml/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.KmlImage(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/jobs", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Jobs(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/MapServer/jobs/{*rest}", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.Jobs(catalog, registry, service, cancellationToken));
    }

    internal static IFeatureStore Store(IStoreRegistry stores, string store) =>
        stores.Features(store);

    internal static IDataCatalogue Catalogue(IStoreRegistry stores, string store) =>
        stores.Catalogue(store);

    /// <summary>The Web-Mercator scheme when registered, else the first scheme (the tileInfo source).</summary>
    internal static ITileScheme? MapTileScheme(IEnumerable<ITileScheme> schemes)
    {
        var registered = schemes.ToArray();
        return registered.FirstOrDefault(scheme => MapServerResources.SridOf(scheme.Crs) == 3857) ?? registered.FirstOrDefault();
    }
}

/// <summary>
/// One MapServer request: the published service, the request context and
/// the store registry every resource handler reads through. Grouping them
/// keeps each handler's signature to the ids and engines its own
/// resource needs.
/// </summary>
internal sealed record MapServerRequest(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    HttpContext Context,
    IStoreRegistry Stores,
    CancellationToken CancellationToken);
