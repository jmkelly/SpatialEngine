using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service routes (spec §8, ADR-0051): root metadata, raster info,
/// catalog item/listing/query, identify, <c>exportImage</c>, and the catalog
/// file surface (<c>download</c>, the Raster Image/Thumbnail/File resources).
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

    internal static void MapImageServer(
        RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, GeoServicesOptions options)
    {
        group.MapMethods("/{service}/ImageServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageServerRoot(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/exportImage", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageExport(catalog, registry, service, context, stores, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/identify", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageIdentify(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/ImageServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageQuery(catalog, registry, service, context, stores, operations, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/download", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            ImageDownload(catalog, registry, service, context, stores, options, cancellationToken));
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
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, stores, cancellationToken);
            return EsriJson.Value(ImageService.Root(image.Description, image.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

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
