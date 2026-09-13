using System.Globalization;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service routes (spec §8, ADR-0051): root metadata, raster info,
/// catalog item and listing, identify and <c>exportImage</c>. The ImageServer
/// is the GeoServices projection of a <see cref="PublicationKind.Image"/>
/// publication whose layer names a dataset in the keyed <c>raster</c> store;
/// the adapter consumes only the SDK <see cref="IRasterCatalogue"/> contract,
/// never a NetVips type or a raster file path.
/// </summary>
internal static class ImageServerEndpoints
{
    internal static void MapImageServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IPublicationRegistry registry)
    {
        group.MapMethods("/{service}/ImageServer", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            ImageServerRoot(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/ImageServer/exportImage", ["GET", "POST"], (
            string service, HttpContext context, IServiceProvider services, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            ImageExport(catalog, registry, service, context, services, transforms, cancellationToken));
        group.MapMethods("/{service}/ImageServer/identify", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            ImageIdentify(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/ImageServer/query", ["GET", "POST"], (string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            ImageQuery(catalog, registry, service, context, services, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}/info", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            RasterInfo(catalog, registry, service, rasterId, context, services, cancellationToken));
        group.MapMethods("/{service}/ImageServer/{rasterId:long}", ["GET", "POST"], (
            string service, long rasterId, HttpContext context, IServiceProvider services, CancellationToken cancellationToken) =>
            RasterItem(catalog, registry, service, rasterId, context, services, cancellationToken));
    }

    private static async Task<IResult> ImageServerRoot(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
            return EsriJson.Value(ImageService.Root(image.Description, image.Copyright));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageExport(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context,
        IServiceProvider services, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var format = parameters.Get("f");
            var stream = string.Equals(format, "image", StringComparison.OrdinalIgnoreCase);
            if (!stream)
            {
                EsriFormat.Ensure(format);
            }

            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
            var rasterSrid = MapService.SridOf(image.Description.Raster.Crs);
            var bbox = MapRenderEngine.ParseBbox(parameters.Get("bbox"));
            var (width, height) = MapRenderEngine.ParseSize(parameters.Get("size") ?? "400,400");
            var bboxCrs = EsriValueParser.ParseSpatialReference(parameters.Get("bboxSR")) ?? CoordinateReference.Epsg(rasterSrid);
            var imageCrs = EsriValueParser.ParseSpatialReference(parameters.Get("imageSR")) ?? bboxCrs;
            var viewport = new RasterViewport(
                MapRenderEngine.Project(bbox, bboxCrs, imageCrs, transforms, cancellationToken), width, height, imageCrs.ToString());
            var request = new RasterExportRequest(
                viewport,
                ImageService.ParseFormat(parameters.Get("format")),
                ImageService.ParseInterpolation(parameters.Get("interpolation")),
                ImageService.ParsePixelType(parameters.Get("pixelType")),
                ImageService.ParseNoData(parameters.Get("noData")),
                ImageService.ParseQuality(parameters.Get("compressionQuality")));
            var exported = await image.Catalogue.ExportAsync(image.Dataset, request, cancellationToken);
            if (stream)
            {
                GeoServicesResponses.WriteImageHeaders(context, exported);
                return Results.Bytes(exported.Content, exported.MediaType);
            }

            return EsriJson.Value(ImageService.Export(
                GeoServicesResponses.ExportHref(context), viewport, MapService.SridOf(imageCrs.ToString())));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ImageIdentify(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
            var rasterCrs = image.Description.Raster.Crs;
            var srid = MapService.SridOf(rasterCrs);
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
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, HttpContext context,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
            RequireCatalog(image.Description, service);
            var items = await image.Catalogue.ListItemsAsync(image.Dataset, cancellationToken);
            var objectIds = parameters.Get("objectIds") is { } ids ? EsriValueParser.ParseInt64s(ids, "objectIds") : null;
            var filtered = objectIds is { Count: > 0 }
                ? items.Where(item => objectIds.Contains(item.ObjectId)).ToArray()
                : items;
            return ImageService.Query(
                image.Description,
                filtered,
                parameters.GetBool("returnIdsOnly", false),
                parameters.GetBool("returnGeometry", true),
                ParseOutFields(parameters.Get("outFields")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> RasterItem(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, long rasterId, HttpContext context,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
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
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, long rasterId, HttpContext context,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var image = await ResolveImageAsync(catalog, registry, service, services, cancellationToken);
            RequireCatalog(image.Description, service);
            var item = await FindItemAsync(image, rasterId, cancellationToken);
            return EsriJson.Value(ImageService.Info(item.Raster));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>Resolves the image publication, its keyed raster catalogue and the described dataset.</summary>
    private static async Task<ImageContext> ResolveImageAsync(
        GeoServicesCatalog catalog, IPublicationRegistry registry, string service, IServiceProvider services, CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesEndpoints.ResolveServiceAsync(catalog, registry, service, "ImageServer", PublicationKind.Image, cancellationToken);
        if (resolved.Layers is not { Count: > 0 } layers)
        {
            throw EsriInteropException.Invalid(
                $"Image Service '{service}' must expose an explicit raster dataset as its layer.");
        }

        var catalogue = services.GetKeyedService<IRasterCatalogue>(resolved.Store)
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

    private static string[]? ParseOutFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*")
        {
            return null;
        }

        var fields = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 0 ? null : fields;
    }
}

/// <summary>The resolved raster catalogue, dataset name, description and copyright of one request.</summary>
internal sealed record ImageContext(
    IRasterCatalogue Catalogue,
    string Dataset,
    RasterDatasetDescription Description,
    string? Copyright);
