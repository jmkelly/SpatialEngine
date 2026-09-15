using System.Text;
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
/// <c>statistics</c>, <c>computeHistograms</c>, <c>rasterAttributeTable</c>,
/// the service-level <c>thumbnail</c>, the authored service-level
/// <c>metadata</c> XML and the per-item <c>{rasterId}/metadata</c> XML
/// (ADR-0068), plus the
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
            ImageFileHandlers.ImageDownload(catalog, registry, service, context, stores, options, cancellationToken));
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
            ImageFileHandlers.ImageFile(catalog, registry, service, context, stores, options, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/info", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterInfo(catalog, registry, service, rasterId, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/image", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterImage(catalog, registry, service, rasterId, context, stores, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/thumbnail", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterThumbnail(catalog, registry, service, rasterId, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/metadata", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            RasterMetadata(catalog, registry, service, rasterId, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageFileHandlers.RasterItem(catalog, registry, service, rasterId, context, stores, cancellationToken));
    }

    private static async Task<IResult> ImageServerRoot(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, GeoServicesOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
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
        EsriErrorMapper.Map(GeoServicesErrors.Invalid($"The '{operation}' operation is not supported: {reason}"));

    private static async Task<IResult> ImageExport(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var rasterId = ImageFileHandlers.ParseExportRasterId(parameters, image.Description, service);
            return await ImageFileHandlers.ExportImageAsync(image, parameters, context, transforms, rasterId, defaultBbox: null, defaultCrs: null, cancellationToken);
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
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
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
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            ImageFileHandlers.RequireCatalog(image.Description, service);
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
            ImageFileHandlers.RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            ImageFileHandlers.RejectExportParameter(parameters, "variable", "multidimensional variables are not supported.");
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var info = image.Description.Raster;
            var bandIds = ImageLegendBuilder.ParseLegendBandIds(parameters.Get("bandIds"), info.BandCount);
            if (info.Extent.IsEmpty)
            {
                throw GeoServicesErrors.Invalid($"Image Service '{service}' has no extent to render a legend from.");
            }

            var viewport = new RasterViewport(info.Extent, LegendSwatchSize, LegendSwatchSize, info.Crs);
            var exported = await image.Catalogue.ExportAsync(
                image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png), cancellationToken);
            return EsriJson.Value(ImageLegendBuilder.Legend(image.Description, exported.Content, exported.Width, exported.Height, bandIds));
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
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            ImageFileHandlers.RequireCatalog(image.Description, service);
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
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var statistics = image.Description.Raster.BandStatistics;
            if (statistics is not { Count: > 0 })
            {
                throw GeoServicesErrors.NotFound($"Image Service '{service}' has no stored band statistics.");
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
                throw GeoServicesErrors.Invalid(
                    $"'geometryType' must be esriGeometryEnvelope or esriGeometryPolygon, got '{geometryType}'.");
            }

            ImageFileHandlers.RejectExportParameter(parameters, "mosaicRule", "on-the-fly mosaicking is not supported.");
            ImageFileHandlers.RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            ImageFileHandlers.RejectExportParameter(parameters, "pixelSize", "histograms are computed at the base resolution.");
            ImageFileHandlers.RejectExportParameter(parameters, "time", "the raster catalog carries no temporal dimension.");
            ImageFileHandlers.RejectExportParameter(parameters, "processAsMultidimensional", "multidimensional rasters are not supported.");
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var rasterCrs = image.Description.Raster.Crs;
            var geometry = EsriValueParser.ParseGeometry(
                parameters.Require("geometry"), CoordinateReference.Epsg(MapServerResources.SridOf(rasterCrs)));
            var bounds = geometry.Envelope ?? Envelope.Empty;
            if (bounds.IsEmpty)
            {
                throw GeoServicesErrors.Invalid("The 'geometry' parameter must cover a non-empty area.");
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
            ImageFileHandlers.RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var table = image.Description.Raster.AttributeTable
                ?? throw GeoServicesErrors.NotFound($"Image Service '{service}' does not have a raster attribute table.");
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

            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            var viewport = ImageService.ThumbnailViewport(image.Description.Raster, ImageFileHandlers.ThumbnailMaxSize);
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
    /// The service-level Metadata (S3 metadata/): the map's authored XML
    /// document served byte-faithful as <c>application/xml</c>, like the
    /// reference. This replaces the T-042 JSON dataset projection (ADR-0068):
    /// without authoring the resource is a typed <c>not.found</c>, never an
    /// invented document. Only an absent format or <c>f=xml</c> passes.
    /// </summary>
    private static async Task<IResult> ImageMetadata(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.EnsureXml(parameters.Get("f"));
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            return MetadataDocument(service, image.MetadataXml);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The per-item Raster Metadata (S3 raster-metadata/): the catalog
    /// item's authored XML document served byte-faithful as
    /// <c>application/xml</c>, like the reference. An item without authored
    /// metadata — or an unknown item, or a service without a catalog — is a
    /// typed <c>not.found</c>/<c>invalid.arguments</c> failure (ADR-0068).
    /// </summary>
    private static async Task<IResult> RasterMetadata(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, long rasterId, HttpContext context,
        IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.EnsureXml(parameters.Get("f"));
            var image = await ImageFileHandlers.ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            ImageFileHandlers.RequireCatalog(image.Description, service);
            var item = await ImageFileHandlers.FindItemAsync(image, rasterId, cancellationToken);
            return item.MetadataXml is null
                ? throw GeoServicesErrors.NotFound($"Raster catalog item {rasterId} does not have authored metadata.")
                : Results.Text(item.MetadataXml, "application/xml", Encoding.UTF8);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Serves one authored metadata document byte-faithful as
    /// <c>application/xml</c>; a service without authoring answers a typed
    /// <c>not.found</c> instead of an invented document (ADR-0068).
    /// </summary>
    private static IResult MetadataDocument(string service, string? xml) =>
        xml is null
            ? throw GeoServicesErrors.NotFound($"Image Service '{service}' does not have authored service metadata.")
            : Results.Text(xml, "application/xml", Encoding.UTF8);

}

/// <summary>The resolved raster catalogue, dataset name, description, copyright and authored service metadata of one request.</summary>
internal sealed record ImageContext(
    IRasterCatalogue Catalogue,
    string Dataset,
    RasterDatasetDescription Description,
    string? Copyright,
    string? MetadataXml = null);
