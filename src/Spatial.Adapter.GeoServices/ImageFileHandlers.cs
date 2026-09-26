using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service file surface: raster download/file, per-item
/// metadata/thumbnail/image resources and export-image rendering. Split
/// out of <see cref="ImageServerEndpoints"/> so the route facade keeps
/// only mapping and the file-serving fan-out (catalogue, raster model,
/// render engine) lives with the code that uses it (ADR-0040).
/// </summary>
internal static class ImageFileHandlers
{
    internal const int ThumbnailMaxSize = 200;

    /// <summary>
    /// One image-resource request: the published Image Service plus the seams
    /// every resource handler needs. Grouping them keeps each handler's
    /// signature to the ids and options its own resource addresses.
    /// </summary>
    internal sealed record ImageRequest(
        GeoServicesCatalog Catalog,
        IMapRegistry Registry,
        string Service,
        HttpContext Context,
        IStoreRegistry Stores,
        CancellationToken CancellationToken);

    /// <summary>
    /// One export the caller has already resolved: the image, the request
    /// parameters, the selected raster and the per-item export defaults.
    /// </summary>
    internal sealed record ImageExportRequest(
        ImageContext Image,
        EsriRequestParameters Parameters,
        ICoordinateTransforms Transforms,
        long? RasterId,
        Envelope? DefaultBbox,
        string? DefaultCrs);
    internal static async Task<IResult> ImageDownload(ImageRequest request, GeoServicesOptions options)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureDownloadAllowed(options);
            var image = await ResolveImageAsync(request);
            RequireCatalog(image.Description, request.Service);
            RejectDownloadClipping(parameters);
            var rasterIds = ImageService.ParseRasterIds(parameters.Get("rasterIds"));
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, request.CancellationToken);
            var files = await CollectFilesAsync(image, items, rasterIds, request.Service, request.CancellationToken);
            EnforceDownloadLimits(files, options);
            return EsriJson.Value(ImageService.Download(files));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> ImageFile(ImageRequest request, GeoServicesOptions options)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
            EnsureDownloadAllowed(options);
            var image = await ResolveImageAsync(request);
            RequireCatalog(image.Description, request.Service);
            var fileId = parameters.Require("id");
            var files = await image.Catalogue.ListFilesAsync(image.Dataset, null, request.CancellationToken);
            var file = files.FirstOrDefault(candidate => string.Equals(candidate.Id, fileId, StringComparison.Ordinal))
                ?? throw GeoServicesErrors.NotFound($"Raster file '{fileId}' does not exist in service '{request.Service}'.");
            if (file.Size > options.MaxRasterDownloadBytes)
            {
                throw GeoServicesErrors.Invalid(
                    $"Raster file '{fileId}' is {file.Size} bytes, over the {options.MaxRasterDownloadBytes}-byte download cap.");
            }

            var content = await image.Catalogue.ReadFileAsync(image.Dataset, fileId, request.CancellationToken);
            return Results.File(content.Content, content.MediaType, content.Name, enableRangeProcessing: true);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> RasterItem(ImageRequest request, long rasterId)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(request);
            RequireCatalog(image.Description, request.Service);
            var item = await FindItemAsync(image, rasterId, request.CancellationToken);
            return ImageService.CatalogItem(
                item, image.Description.CatalogSchema!, image.Description.ObjectIdField!, parameters.GetBool("returnGeometry", true));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> RasterInfo(ImageRequest request, long rasterId)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(request);
            RequireCatalog(image.Description, request.Service);
            var item = await FindItemAsync(image, rasterId, request.CancellationToken);
            return EsriJson.Value(ImageService.Info(item.Raster));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The Raster Image resource (spec §8.2): one catalog item over the export pipeline.</summary>
    internal static async Task<IResult> RasterImage(ImageRequest request, long rasterId, ICoordinateTransforms transforms)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
            var image = await ResolveImageAsync(request);
            RequireCatalog(image.Description, request.Service);
            var item = await FindItemAsync(image, rasterId, request.CancellationToken);
            return await ExportImageAsync(
                new ImageExportRequest(image, parameters, transforms, rasterId, item.Raster.Extent, item.Raster.Crs),
                request.Context,
                request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The Raster Thumbnail resource (spec §8.3): a reduced image of one catalog item, streamed.</summary>
    internal static async Task<IResult> RasterThumbnail(ImageRequest request, long rasterId)
    {
        try
        {
            return await ThumbnailAsync(request, rasterId);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ThumbnailAsync(ImageRequest request, long rasterId)
    {
        var parameters = await EsriRequestParameters.ReadAsync(request.Context, request.CancellationToken);
        var format = parameters.Get("f");
        var stream = IsImageStream(format);
        if (!stream)
        {
            EsriFormat.Ensure(format);
        }

        var image = await ResolveImageAsync(request);
        RequireCatalog(image.Description, request.Service);
        var item = await FindItemAsync(image, rasterId, request.CancellationToken);
        var viewport = ImageService.ThumbnailViewport(item.Raster, ThumbnailMaxSize);
        var exported = await image.Catalogue.ExportAsync(
            image.Dataset, new RasterExportRequest(viewport, RasterFormat.Png, RasterId: rasterId), request.CancellationToken);
        return ThumbnailResult(request.Context, exported, item, viewport, stream);
    }

    /// <summary>No <c>f</c> (or <c>f=image</c>) streams the pixels; any other format is a JSON export descriptor.</summary>
    private static bool IsImageStream(string? format) =>
        string.IsNullOrWhiteSpace(format) || string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);

    private static IResult ThumbnailResult(
        HttpContext context, RasterImage exported, RasterCatalogItem item, RasterViewport viewport, bool stream) =>
        stream
            ? StreamThumbnail(context, exported)
            : EsriJson.Value(ImageService.Export(
                GeoServicesResponses.ExportHref(context), viewport, MapServerResources.SridOf(item.Raster.Crs)));

    private static IResult StreamThumbnail(HttpContext context, RasterImage exported)
    {
        GeoServicesResponses.WriteImageHeaders(context, exported);
        return Results.Bytes(exported.Content, exported.MediaType);
    }

    /// <summary>
    /// The shared export pipeline of <c>exportImage</c> and the Raster Image
    /// resource: parse the viewport, apply the optional <c>rasterId</c> and
    /// stream bytes (<c>f=image</c>) or return the JSON <c>href</c>.
    /// </summary>
    internal static async Task<IResult> ExportImageAsync(
        ImageExportRequest export, HttpContext context, CancellationToken cancellationToken)
    {
        var image = export.Image;
        var parameters = export.Parameters;
        var transforms = export.Transforms;
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
            ? MapRenderParameters.ParseBbox(value)
            : export.DefaultBbox ?? throw GeoServicesErrors.Invalid("The 'bbox' parameter is required.");
        var defaultSrid = bboxValue is null && export.DefaultCrs is not null ? MapServerResources.SridOf(export.DefaultCrs) : rasterSrid;
        var (width, height) = MapRenderParameters.ParseSize(parameters.Get("size") ?? "400,400");
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
            RasterId: export.RasterId);
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
    internal static Task<ImageContext> ResolveImageAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, IStoreRegistry stores, CancellationToken cancellationToken) =>
        ResolveImagePublicationAsync(catalog, registry, service, stores, cancellationToken);

    private static Task<ImageContext> ResolveImageAsync(ImageRequest request) =>
        ResolveImagePublicationAsync(request.Catalog, request.Registry, request.Service, request.Stores, request.CancellationToken);

    private static async Task<ImageContext> ResolveImagePublicationAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, IStoreRegistry stores,
        CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "ImageServer", MapServiceKind.ImageServer, cancellationToken);
        if (resolved.Layers is not { Count: > 0 } layers)
        {
            throw GeoServicesErrors.Invalid(
                $"Image Service '{service}' must expose an explicit raster dataset as its layer.");
        }

        var catalogue = stores.RasterCatalogue(resolved.Store)
            ?? throw GeoServicesErrors.ServiceUnavailable($"No raster provider is configured for store '{resolved.Store}'.");
        var dataset = layers.OrderBy(layer => layer.LayerId).First().Dataset;
        var description = await catalogue.DescribeAsync(dataset, cancellationToken);
        return new ImageContext(catalogue, dataset, description, resolved.Copyright, resolved.MetadataXml);
    }

    internal static async Task<RasterCatalogItem> FindItemAsync(ImageContext image, long rasterId, CancellationToken cancellationToken)
    {
        var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
        return items.FirstOrDefault(item => item.ObjectId == rasterId)
            ?? throw GeoServicesErrors.NotFound($"Raster catalog item {rasterId} does not exist.");
    }

    internal static void RequireCatalog(RasterDatasetDescription description, string service)
    {
        if (!description.HasCatalog)
        {
            throw GeoServicesErrors.Invalid($"Image Service '{service}' does not include an accessible raster catalog.");
        }
    }

    /// <summary>
    /// Rejects raster-selection parameters that would change the exported
    /// pixels (mosaic method, raster functions, band selection): the engine
    /// serves one raster's native bands, so honouring them is out of scope
    /// and ignoring them would serve wrong bytes.
    /// </summary>
    internal static void RejectRasterSelection(EsriRequestParameters parameters)
    {
        RejectExportParameter(parameters, "mosaicRule", "on-the-fly mosaicking is not supported; address one raster via rasterIds.");
        RejectExportParameter(parameters, "renderingRule", "raster functions are not supported.");
        RejectExportParameter(parameters, "bandIds", "band selection is not supported; all bands are served.");
    }

    internal static void RejectExportParameter(EsriRequestParameters parameters, string name, string reason)
    {
        if (parameters.Has(name))
        {
            throw GeoServicesErrors.Invalid($"The '{name}' parameter is not supported: {reason}");
        }
    }

    /// <summary>The optional single <c>rasterId</c>/<c>rasterIds</c> of an export; many is a non-goal.</summary>
    internal static long? ParseExportRasterId(EsriRequestParameters parameters, RasterDatasetDescription description, string service)
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
            : throw GeoServicesErrors.Invalid("'rasterIds' must name exactly one raster; on-the-fly mosaicking is not supported.");
    }

    internal static async Task<List<(RasterFile File, long RasterId)>> CollectFilesAsync(
        ImageContext image, IReadOnlyList<RasterCatalogItem> items, IReadOnlyList<long> rasterIds, string service, CancellationToken cancellationToken)
    {
        var known = items.ToDictionary(item => item.ObjectId);
        var files = new List<(RasterFile File, long RasterId)>();
        foreach (var rasterId in rasterIds)
        {
            if (!known.ContainsKey(rasterId))
            {
                throw GeoServicesErrors.NotFound($"Raster {rasterId} does not exist in service '{service}'.");
            }

            foreach (var file in await image.Catalogue.ListFilesAsync(image.Dataset, rasterId, cancellationToken))
            {
                files.Add((file, rasterId));
            }
        }

        return files;
    }

    internal static void EnforceDownloadLimits(List<(RasterFile File, long RasterId)> files, GeoServicesOptions options)
    {
        if (files.Count > options.MaxRasterDownloadFiles)
        {
            throw GeoServicesErrors.Invalid(
                $"The download names {files.Count} files, over the {options.MaxRasterDownloadFiles}-file cap.");
        }

        long total = 0;
        foreach (var (file, _) in files)
        {
            total += file.Size;
            if (total > options.MaxRasterDownloadBytes)
            {
                throw GeoServicesErrors.Invalid(
                    $"The download is over the {options.MaxRasterDownloadBytes}-byte cap; request fewer or smaller rasters.");
            }
        }
    }

    internal static void EnsureDownloadAllowed(GeoServicesOptions options)
    {
        if (!options.AllowRasterDownload)
        {
            throw GeoServicesErrors.Invalid(
                "Raw raster download is disabled; enable Spatial:GeoServices:AllowRasterDownload to serve the Raster File and Download Rasters resources.");
        }
    }

    /// <summary>Clipping a raw download and re-encoding it are explicit non-goals (I5); reject them.</summary>
    internal static void RejectDownloadClipping(EsriRequestParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.Get("geometry")))
        {
            throw GeoServicesErrors.Invalid("Clipping a download is not supported; request the unclipped raster files.");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Get("format")))
        {
            throw GeoServicesErrors.Invalid(
                $"Download format '{parameters.Get("format")}' is not supported; raw files are served in their native format.");
        }
    }
}
