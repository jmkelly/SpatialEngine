using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;
namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service routes (spec §8, ADR-0051/0054): root metadata, raster
/// info, catalog item/listing/query, identify, <c>exportImage</c>, the catalog
/// file surface (<c>download</c>, the Raster Image/Thumbnail/File resources),
/// and the missing-resource closeout: <c>legend</c>, <c>find</c>, stored
/// <c>statistics</c>, <c>computeHistograms</c>, <c>rasterAttributeTable</c>,
/// the service-level <c>thumbnail</c>, the authored service-level
/// <c>metadata</c> XML and the per-item <c>{rasterId}/metadata</c> XML
/// (ADR-0068), plus the
/// offline rejects <c>exportTiles</c> / <c>estimateExportTileSize</c>
/// (ADR-0060: packaging needs a job model the host does not have).
/// The ImageServer is the GeoServices projection of a
/// <see cref="MapServiceKind.ImageServer"/> publication whose layer names a dataset
/// in the keyed <c>raster</c> store; the adapter consumes only the SDK
/// <see cref="IRasterCatalogue"/> contract, never a NetVips type or a raster
/// file path. This type maps the routes; each resource surface owns its own
/// handlers — <see cref="ImageServerRootResources"/> (root, exportImage),
/// <see cref="ImageQueryResources"/> (identify, query, find),
/// <see cref="ImageBandResources"/> (stored statistics, histograms,
/// attribute table), <see cref="ImageSampleResources"/> (legend, thumbnail), <see cref="ImageMetadataResources"/> (authored XML),
/// <see cref="ImageFileHandlers"/> (the file surface) — so the mapping keeps
/// only mapping.
/// </summary>
internal static class ImageServerEndpoints
{
    /// <summary>Why mensuration operations are rejected: the catalog carries no sensor models.</summary>
    private const string MensurationReason = "mensuration needs sensor models the raster catalog does not carry.";

    /// <summary>Why multidimensional operations are rejected: only flat rasters are served.</summary>
    private const string MultidimensionalReason = "multidimensional rasters are not supported.";

    /// <summary>Why catalog writes are rejected: ingest is the neutral write path (ADR-0041).</summary>
    private const string CatalogWriteReason = "the raster catalog is read-only; ingest is the neutral write path.";

    internal static void MapImageServer(
        RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, GeoServicesOptions options)
    {
        group.MapMethods("/{service}/ImageServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageServerRootResources.Root(new(catalog, registry, service, context, stores, cancellationToken), options));
        group.MapMethods("/{service}/ImageServer/exportImage", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            ImageServerRootResources.Export(new(catalog, registry, service, context, stores, cancellationToken), transforms, loggerFactory));
        group.MapMethods("/{service}/ImageServer/exportTiles", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.ImageExportTiles(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/ImageServer/estimateExportTileSize", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.ImageEstimateExportTileSize(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/ImageServer/identify", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageQueryResources.Identify(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageQueryResources.Query(new(catalog, registry, service, context, stores, cancellationToken), operations, transforms));
        group.MapMethods("/{service}/ImageServer/download", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.ImageDownload(new(catalog, registry, service, context, stores, cancellationToken), options));
        group.MapMethods("/{service}/ImageServer/legend", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageSampleResources.Legend(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/find", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageQueryResources.Find(new(catalog, registry, service, context, stores, cancellationToken), transforms));
        group.MapMethods("/{service}/ImageServer/statistics", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageBandResources.Statistics(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/computeHistograms", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageBandResources.ComputeHistograms(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/rasterAttributeTable", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageBandResources.AttributeTable(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/thumbnail", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageSampleResources.Thumbnail(new(catalog, registry, service, context, stores, cancellationToken)));
        group.MapMethods("/{service}/ImageServer/metadata", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageMetadataResources.Service(new(catalog, registry, service, context, stores, cancellationToken)));
        // T-043 honesty rejects: mensuration needs sensor models the raster
        // catalog does not carry; multidimensional rasters are not served;
        // the catalog is read-only (ingest is the neutral write path,
        // ADR-0041). Each names its operation instead of falling through to
        // a generic 404 (research S3 measure*/compute-angles/project-*/
        // image-to-map*/query-gps/query-boundary, multidimensional-info/+
        // slices/, add-rasters/delete-rasters/update-raster/uploads).
        MapRejected(group, "measure", MensurationReason);
        MapRejected(group, "measureFromImage", MensurationReason);
        MapRejected(group, "computeAngles", MensurationReason);
        MapRejected(group, "project", MensurationReason);
        MapRejected(group, "projectImage", MensurationReason);
        MapRejected(group, "imageToMap", MensurationReason);
        MapRejected(group, "mapToImage", MensurationReason);
        MapRejected(group, "queryGPSInfo", MensurationReason);
        MapRejected(group, "queryBoundary", MensurationReason);
        MapRejected(group, "multidimensionalInfo", MultidimensionalReason);
        MapRejected(group, "slices", MultidimensionalReason);
        MapRejected(group, "addRasters", CatalogWriteReason);
        MapRejected(group, "deleteRasters", CatalogWriteReason);
        MapRejected(group, "updateRaster", CatalogWriteReason);
        MapRejected(group, "uploads", CatalogWriteReason);
        MapRejected(group, "upload", CatalogWriteReason);
        group.MapMethods("/{service}/ImageServer/file", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.ImageFile(new(catalog, registry, service, context, stores, cancellationToken), options));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/info", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterInfo(new(catalog, registry, service, context, stores, cancellationToken), rasterId));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/image", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterImage(new(catalog, registry, service, context, stores, cancellationToken), rasterId, transforms));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/thumbnail", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterThumbnail(new(catalog, registry, service, context, stores, cancellationToken), rasterId));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/metadata", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageMetadataResources.Item(new(catalog, registry, service, context, stores, cancellationToken), rasterId));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterItem(new(catalog, registry, service, context, stores, cancellationToken), rasterId));
    }

    /// <summary>
    /// Registers one explicitly unsupported operation: a GET+POST route that
    /// answers 400 naming the operation, so clients get an Esri envelope
    /// instead of a generic 404. The rejection is synchronous (no provider
    /// call), so there is nothing to cancel.
    /// </summary>
    private static void MapRejected(RouteGroupBuilder group, string operation, string reason) =>
        group.MapMethods("/{service}/ImageServer/" + operation, ["GET", "POST"], () => RejectNotSupported(operation, reason));

    /// <summary>Rejects one unsupported operation by name with its reason.</summary>
    private static IResult RejectNotSupported(string operation, string reason) =>
        EsriErrorMapper.Map(GeoServicesErrors.Invalid($"The '{operation}' operation is not supported: {reason}"));
}
