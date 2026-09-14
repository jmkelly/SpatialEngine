using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service routes (spec §8, ADR-0051/0054): root metadata, raster
/// info, catalog item/listing/query, identify, <c>exportImage</c>, the catalog
/// file surface (<c>download</c>, the Raster Image/Thumbnail/File resources),
/// and the missing-resource closeout: <c>legend</c>, <c>find</c>, stored
/// <c>statistics</c>, <c>computeHistograms</c>, <c>rasterAttributeTable</c>
/// and the service-level <c>thumbnail</c> and <c>metadata</c>, plus the
/// offline rejects <c>exportTiles</c> / <c>estimateExportTileSize</c>
/// (ADR-0060: packaging needs a job model the host does not have).
/// The ImageServer is the GeoServices projection of a
/// <see cref="MapService.Image"/> publication whose layer names a dataset
/// in the keyed <c>raster</c> store; the adapter consumes only the SDK
/// <see cref="IRasterCatalogue"/> contract, never a NetVips type or a raster
/// file path.
/// </summary>
internal static class ImageServerEndpoints
{
    /// <summary>The longest side of a Raster Thumbnail (spec §8.3); the aspect ratio is preserved.</summary>
    private const int ThumbnailMaxSize = 200;

    /// <summary>Why mensuration operations are rejected: the catalog carries no sensor models.</summary>
    private const string MensurationReason = "mensuration needs sensor models the raster catalog does not carry.";

    /// <summary>Why multidimensional operations are rejected: only flat rasters are served.</summary>
    private const string MultidimensionalReason = "multidimensional rasters are not supported.";

    /// <summary>Why catalog writes are rejected: ingest is the neutral write path (ADR-0041).</summary>
    private const string CatalogWriteReason = "the raster catalog is read-only; ingest is the neutral write path.";

    /// <summary>The square side of a legend swatch (the reference serves 20x20 symbols).</summary>
    private const int LegendSwatchSize = 20;

    internal static void MapImageServer(
        RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, GeoServicesOptions options)
    {
        group.MapMethods("/{service}/ImageServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageServerRoot(catalog, registry, service, context, stores, options, cancellationToken));
        group.MapMethods("/{service}/ImageServer/exportImage", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageExport(catalog, registry, service, context, stores, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/exportTiles", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.ImageExportTiles(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/ImageServer/estimateExportTileSize", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            MapOfflineRejects.ImageEstimateExportTileSize(catalog, registry, service, cancellationToken));
        group.MapMethods("/{service}/ImageServer/identify", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageIdentify(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageQuery(catalog, registry, service, context, stores, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/download", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageDownload(catalog, registry, service, context, stores, options, cancellationToken));
        group.MapMethods("/{service}/ImageServer/legend", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageLegend(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/find", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageFind(catalog, registry, service, context, stores, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/statistics", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageStatistics(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/computeHistograms", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageComputeHistograms(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/rasterAttributeTable", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageAttributeTable(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/thumbnail", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageThumbnail(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/metadata", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageMetadata(catalog, registry, service, context, stores, cancellationToken));
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
            ImageFile(catalog, registry, service, context, stores, options, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/info", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            RasterInfo(catalog, registry, service, rasterId, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/image", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            RasterImage(catalog, registry, service, rasterId, context, stores, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/thumbnail", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            RasterThumbnail(catalog, registry, service, rasterId, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            RasterItem(catalog, registry, service, rasterId, context, stores, cancellationToken));
    }

    private static async Task<IResult> ImageServerRoot(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, GeoServicesOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            return EsriJson.Value(ImageService.Root(image.Description, image.Copyright, options));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
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
        EsriErrorMapper.Map(EsriInteropException.Invalid($"The '{operation}' operation is not supported: {reason}"));

    private static async Task<IResult> ImageExport(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var rasterId = ParseExportRasterId(parameters, image.Description, service);
            return await ExportImageAsync(image, parameters, context, transforms, rasterId, defaultBbox: null, defaultCrs: null, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageIdentify(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var rasterCrs = image.Description.Raster.Crs;
            var srid = MapServerResources.SridOf(rasterCrs);
            var geometry = EsriValueParser.ParseGeometry(parameters.Require("geometry"), CoordinateReference.Epsg(srid));
            var request = new RasterIdentifyRequest(geometry, geometry.CoordinateReference?.ToString() ?? rasterCrs);
            var result = await image.Catalogue.IdentifyAsync(image.Dataset, request, cancellationToken);
            var envelope = geometry.Envelope ?? Envelope.Empty;
            return ImageService.Identify(image.Description, result, envelope.CenterX, envelope.CenterY);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageQuery(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var layerCrs = EsriLayerModel.LayerCoordinateReference(MapServerResources.SridOf(image.Description.Raster.Crs));
            var query = EsriFeatureQuery.Parse(parameters, layerCrs);
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
            return RasterCatalogQuery.Query(image.Description, items, query, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The Legend resource (S3 legend-image-service/): one entry per band
    /// with the 20x20 dataset render as the swatch. The render is what
    /// <c>exportImage</c> serves; <c>renderingRule</c>/<c>variable</c> would
    /// change the symbology and are rejected by name.
    /// </summary>
    private static async Task<IResult> ImageLegend(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            RejectExportParameter(parameters, "variable", "multidimensional variables are not supported.");
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var info = image.Description.Raster;
            var bandIds = ImageService.ParseLegendBandIds(parameters.Get("bandIds"), info.BandCount);
            if (info.Extent.IsEmpty)
            {
                throw EsriInteropException.Invalid($"Image Service '{service}' has no extent to render a legend from.");
            }

            var viewport = new RasterViewport(info.Extent, LegendSwatchSize, LegendSwatchSize, info.Crs);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png), cancellationToken);
            return EsriJson.Value(ImageService.Legend(image.Description, exported.Content, exported.Width, exported.Height, bandIds));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The catalog text search over the raster catalog's string fields.</summary>
    private static async Task<IResult> ImageFind(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
            return ImageFindEngine.Find(image.Description, items, parameters, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The stored band statistics resource (S3 statistics/): the dataset's
    /// configured statistics, or a typed <c>not.found</c> when the dataset
    /// carries none. Computation is the <c>computeHistograms</c> path.
    /// </summary>
    private static async Task<IResult> ImageStatistics(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var statistics = image.Description.Raster.BandStatistics;
            if (statistics is not { Count: > 0 })
            {
                throw new EsriInteropException(
                    EsriErrorCodes.NotFound, $"Image Service '{service}' has no stored band statistics.");
            }

            return EsriJson.Value(ImageService.Statistics(statistics));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The Compute Histograms operation (spec §8): per-band histograms over
    /// the requested envelope or polygon. Mosaic, rendering, pixel-size,
    /// time and multidimensional selectors would change the pixels and are
    /// rejected by name; the provider projects and clips to the raster.
    /// </summary>
    private static async Task<IResult> ImageComputeHistograms(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var geometryType = parameters.Require("geometryType");
            if (!string.Equals(geometryType, "esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(geometryType, "esriGeometryPolygon", StringComparison.OrdinalIgnoreCase))
            {
                throw EsriInteropException.Invalid(
                    $"'geometryType' must be esriGeometryEnvelope or esriGeometryPolygon, got '{geometryType}'.");
            }

            RejectExportParameter(parameters, "mosaicRule", "on-the-fly mosaicking is not supported.");
            RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            RejectExportParameter(parameters, "pixelSize", "histograms are computed at the base resolution.");
            RejectExportParameter(parameters, "time", "the raster catalog carries no temporal dimension.");
            RejectExportParameter(parameters, "processAsMultidimensional", "multidimensional rasters are not supported.");
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var rasterCrs = image.Description.Raster.Crs;
            var geometry = EsriValueParser.ParseGeometry(
                parameters.Require("geometry"), CoordinateReference.Epsg(MapServerResources.SridOf(rasterCrs)));
            var bounds = geometry.Envelope ?? Envelope.Empty;
            if (bounds.IsEmpty)
            {
                throw EsriInteropException.Invalid("The 'geometry' parameter must cover a non-empty area.");
            }

            var histograms = await image.Catalogue.ComputeHistogramsAsync(
                image.Dataset,
                new RasterHistogramRequest(bounds, geometry.CoordinateReference?.ToString() ?? rasterCrs),
                cancellationToken);
            return EsriJson.Value(ImageService.Histograms(histograms));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The raster attribute table resource (S3 raster-attribute-table/): the
    /// configured value-frequency table, or a typed <c>not.found</c> when
    /// the dataset carries none (the resource exists only if the raster has
    /// a table, like the reference).
    /// </summary>
    private static async Task<IResult> ImageAttributeTable(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var table = image.Description.Raster.AttributeTable
                ?? throw new EsriInteropException(
                    EsriErrorCodes.NotFound, $"Image Service '{service}' does not have a raster attribute table.");
            return ImageService.AttributeTable(table);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The service-level Thumbnail: the whole dataset reduced like one
    /// catalog item's thumbnail (spec §8.3), streamed or as a JSON href.
    /// </summary>
    private static async Task<IResult> ImageThumbnail(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var format = parameters.Get("f");
            var stream = string.IsNullOrWhiteSpace(format) || string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);
            if (!stream)
            {
                EsriFormat.Ensure(format);
            }

            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var viewport = ImageService.ThumbnailViewport(image.Description.Raster, ThumbnailMaxSize);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png), cancellationToken);
            if (stream)
            {
                GeoServicesResponses.WriteImageHeaders(context, exported);
                return Results.Bytes(exported.Content, exported.MediaType);
            }

            return EsriJson.Value(ImageService.Export(
                GeoServicesResponses.ExportHref(context), viewport, MapServerResources.SridOf(image.Description.Raster.Crs)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The service-level Metadata: the described dataset as JSON. The engine
    /// keeps no authored (ISO/FGDC) metadata store, so unlike the reference
    /// this is a JSON projection, not an XML document (ADR-0054).
    /// </summary>
    private static async Task<IResult> ImageMetadata(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            return EsriJson.Value(ImageService.Metadata(image.Description, image.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageDownload(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, GeoServicesOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureDownloadAllowed(options);
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            RejectDownloadClipping(parameters);
            var rasterIds = ImageService.ParseRasterIds(parameters.Get("rasterIds"));
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
            var files = await CollectFilesAsync(image, items, rasterIds, service, cancellationToken);
            EnforceDownloadLimits(files, options);
            return EsriJson.Value(ImageService.Download(files));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageFile(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, GeoServicesOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EnsureDownloadAllowed(options);
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var fileId = parameters.Require("id");
            var files = await image.Catalogue.ListFilesAsync(image.Dataset, null, cancellationToken);
            var file = files.FirstOrDefault(candidate => string.Equals(candidate.Id, fileId, StringComparison.Ordinal))
                ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Raster file '{fileId}' does not exist in service '{service}'.");
            if (file.Size > options.MaxRasterDownloadBytes)
            {
                throw EsriInteropException.Invalid(
                    $"Raster file '{fileId}' is {file.Size} bytes, over the {options.MaxRasterDownloadBytes}-byte download cap.");
            }

            var content = await image.Catalogue.ReadFileAsync(image.Dataset, fileId, cancellationToken);
            return Results.File(content.Content, content.MediaType, content.Name, enableRangeProcessing: true);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> RasterItem(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, long rasterId, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var item = await FindItemAsync(image, rasterId, cancellationToken);
            return ImageService.CatalogItem(
                item, image.Description.CatalogSchema!, image.Description.ObjectIdField!, parameters.GetBool("returnGeometry", true));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> RasterInfo(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, long rasterId, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var item = await FindItemAsync(image, rasterId, cancellationToken);
            return EsriJson.Value(ImageService.Info(item.Raster));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The Raster Image resource (spec §8.2): one catalog item over the export pipeline.</summary>
    private static async Task<IResult> RasterImage(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, long rasterId, HttpContext context,
        IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var item = await FindItemAsync(image, rasterId, cancellationToken);
            return await ExportImageAsync(image, parameters, context, transforms, rasterId, item.Raster.Extent, item.Raster.Crs, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The Raster Thumbnail resource (spec §8.3): a reduced image of one catalog item, streamed.</summary>
    private static async Task<IResult> RasterThumbnail(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, long rasterId, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var format = parameters.Get("f");
            var stream = string.IsNullOrWhiteSpace(format) || string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);
            if (!stream)
            {
                EsriFormat.Ensure(format);
            }

            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            RequireCatalog(image.Description, service);
            var item = await FindItemAsync(image, rasterId, cancellationToken);
            var viewport = ImageService.ThumbnailViewport(item.Raster, ThumbnailMaxSize);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png, RasterId: rasterId), cancellationToken);
            if (stream)
            {
                GeoServicesResponses.WriteImageHeaders(context, exported);
                return Results.Bytes(exported.Content, exported.MediaType);
            }

            return EsriJson.Value(ImageService.Export(
                GeoServicesResponses.ExportHref(context), viewport, MapServerResources.SridOf(item.Raster.Crs)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The shared export pipeline of <c>exportImage</c> and the Raster Image
    /// resource: parse the viewport, apply the optional <c>rasterId</c> and
    /// stream bytes (<c>f=image</c>) or return the JSON <c>href</c>.
    /// </summary>
    private static async Task<IResult> ExportImageAsync(
        ImageContext image, EsriRequestParameters parameters, HttpContext context, ICoordinateTransforms transforms,
        long? rasterId, Envelope? defaultBbox, string? defaultCrs, CancellationToken cancellationToken)
    {
        // T-015 closeout: raster-selection parameters change which pixels
        // combine; silently ignoring them would serve wrong bytes.
        RejectRasterSelection(parameters);
        var format = parameters.Get("f");
        var stream = string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);
        if (!stream)
        {
            EsriFormat.Ensure(format);
        }

        var rasterSrid = MapServerResources.SridOf(image.Description.Raster.Crs);
        var bboxValue = parameters.Get("bbox");
        var bbox = bboxValue is { Length: > 0 } value
            ? MapRenderEngine.ParseBbox(value)
            : defaultBbox ?? throw EsriInteropException.Invalid("The 'bbox' parameter is required.");
        var defaultSrid = bboxValue is null && defaultCrs is not null ? MapServerResources.SridOf(defaultCrs) : rasterSrid;
        var (width, height) = MapRenderEngine.ParseSize(parameters.Get("size") ?? "400,400");
        var bboxCrs = EsriValueParser.ParseSpatialReference(parameters.Get("bboxSR")) ?? CoordinateReference.Epsg(defaultSrid);
        var imageCrs = EsriValueParser.ParseSpatialReference(parameters.Get("imageSR")) ?? bboxCrs;
        var viewport = new RasterViewport(
            MapRenderEngine.Project(bbox, bboxCrs, imageCrs, transforms, cancellationToken), width, height, imageCrs.ToString());
        var request = new RasterExportRequest(
            viewport,
            ImageService.ParseFormat(parameters.Get("format")),
            ImageService.ParseInterpolation(parameters.Get("interpolation")),
            ImageService.ParsePixelType(parameters.Get("pixelType")),
            ImageService.ParseNoData(parameters.Get("noData")),
            ImageService.ParseQuality(parameters.Get("compressionQuality")),
            RasterId: rasterId);
        var exported = await image.Catalogue.ExportAsync(image.Dataset, request, cancellationToken);
        if (stream)
        {
            GeoServicesResponses.WriteImageHeaders(context, exported);
            return Results.Bytes(exported.Content, exported.MediaType);
        }

        return EsriJson.Value(ImageService.Export(
            GeoServicesResponses.ExportHref(context), viewport, MapServerResources.SridOf(imageCrs.ToString())));
    }

    /// <summary>Resolves the image publication, its keyed raster catalogue and the described dataset.</summary>
    private static async Task<ImageContext> ResolveImageAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "ImageServer", MapService.Image, cancellationToken);
        if (resolved.Layers is not { Count: > 0 } layers)
        {
            throw EsriInteropException.Invalid(
                $"Image Service '{service}' must expose an explicit raster dataset as its layer.");
        }

        var catalogue = stores.RasterCatalogue(resolved.Store)
            ?? throw new EsriInteropException(
                EsriErrorCodes.ServiceUnavailable, $"No raster provider is configured for store '{resolved.Store}'.");
        var dataset = layers.OrderBy(layer => layer.LayerId).First().Dataset;
        var description = await catalogue.DescribeAsync(dataset, cancellationToken);
        return new ImageContext(catalogue, dataset, description, resolved.Copyright);
    }

    private static async Task<RasterCatalogItem> FindItemAsync(ImageContext image, long rasterId, CancellationToken cancellationToken)
    {
        var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
        return items.FirstOrDefault(item => item.ObjectId == rasterId)
            ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Raster catalog item {rasterId} does not exist.");
    }

    private static void RequireCatalog(RasterDatasetDescription description, string service)
    {
        if (!description.HasCatalog)
        {
            throw EsriInteropException.Invalid($"Image Service '{service}' does not include an accessible raster catalog.");
        }
    }

    /// <summary>
    /// Rejects raster-selection parameters that would change the exported
    /// pixels (mosaic method, raster functions, band selection): the engine
    /// serves one raster's native bands, so honouring them is out of scope
    /// and ignoring them would serve wrong bytes.
    /// </summary>
    private static void RejectRasterSelection(EsriRequestParameters parameters)
    {
        RejectExportParameter(parameters, "mosaicRule", "on-the-fly mosaicking is not supported; address one raster via rasterIds.");
        RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
        RejectExportParameter(parameters, "bandIds", "band selection is not supported; all bands are served.");
    }

    private static void RejectExportParameter(EsriRequestParameters parameters, string name, string reason)
    {
        if (parameters.Has(name))
        {
            throw EsriInteropException.Invalid($"The '{name}' parameter is not supported: {reason}");
        }
    }

    /// <summary>The optional single <c>rasterId</c>/<c>rasterIds</c> of an export; many is a non-goal.</summary>
    private static long? ParseExportRasterId(EsriRequestParameters parameters, RasterDatasetDescription description, string service)
    {
        var value = parameters.Get("rasterIds") ?? parameters.Get("rasterId");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        RequireCatalog(description, service);
        var ids = EsriValueParser.ParseInt64s(value, "rasterIds");
        return ids.Count == 1
            ? ids[0]
            : throw EsriInteropException.Invalid("'rasterIds' must name exactly one raster; on-the-fly mosaicking is not supported.");
    }

    private static async Task<List<(RasterFile File, long RasterId)>> CollectFilesAsync(
        ImageContext image, IReadOnlyList<RasterCatalogItem> items, IReadOnlyList<long> rasterIds, string service, CancellationToken cancellationToken)
    {
        var known = items.ToDictionary(item => item.ObjectId);
        var files = new List<(RasterFile File, long RasterId)>();
        foreach (var rasterId in rasterIds)
        {
            if (!known.ContainsKey(rasterId))
            {
                throw new EsriInteropException(EsriErrorCodes.NotFound, $"Raster {rasterId} does not exist in service '{service}'.");
            }

            foreach (var file in await image.Catalogue.ListFilesAsync(image.Dataset, rasterId, cancellationToken))
            {
                files.Add((file, rasterId));
            }
        }

        return files;
    }

    private static void EnforceDownloadLimits(List<(RasterFile File, long RasterId)> files, GeoServicesOptions options)
    {
        if (files.Count > options.MaxRasterDownloadFiles)
        {
            throw EsriInteropException.Invalid(
                $"The download names {files.Count} files, over the {options.MaxRasterDownloadFiles}-file cap.");
        }

        long total = 0;
        foreach (var (file, _) in files)
        {
            total += file.Size;
            if (total > options.MaxRasterDownloadBytes)
            {
                throw EsriInteropException.Invalid(
                    $"The download is over the {options.MaxRasterDownloadBytes}-byte cap; request fewer or smaller rasters.");
            }
        }
    }

    private static void EnsureDownloadAllowed(GeoServicesOptions options)
    {
        if (!options.AllowRasterDownload)
        {
            throw EsriInteropException.Invalid(
                "Raw raster download is disabled; enable Spatial:GeoServices:AllowRasterDownload to serve the Raster File and Download Rasters resources.");
        }
    }

    /// <summary>Clipping a raw download and re-encoding it are explicit non-goals (I5); reject them.</summary>
    private static void RejectDownloadClipping(EsriRequestParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.Get("geometry")))
        {
            throw EsriInteropException.Invalid("Clipping a download is not supported; request the unclipped raster files.");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Get("format")))
        {
            throw EsriInteropException.Invalid(
                $"Download format '{parameters.Get("format")}' is not supported; raw files are served in their native format.");
        }
    }
}

/// <summary>The resolved raster catalogue, dataset name, description and copyright of one request.</summary>
internal sealed record ImageContext(
    IRasterCatalogue Catalogue,
    string Dataset,
    RasterDatasetDescription Description,
    string? Copyright);
