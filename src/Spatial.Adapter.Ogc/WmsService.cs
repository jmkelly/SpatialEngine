using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The OGC Web Map Service 1.3.0 projection (ADR-0052 §3): GetCapabilities,
/// GetMap and GetFeatureInfo over a map's feature layers. GetMap renders the
/// map's persisted style through <see cref="IMapRenderer"/> in the requested
/// CRS/bbox/size; GetFeatureInfo queries the selected layers through
/// <see cref="IFeatureStore.QueryAsync"/> near the clicked pixel. The
/// projection is read-only and never sees a protocol type outside this file.
/// </summary>
internal static class WmsService
{
    public static async Task<IResult> HandleAsync(
        string name, OgcParameters parameters, OgcRequestServices services, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        parameters.RequiredService("WMS");
        var map = await services.ResolveMapAsync(name, MapService.Wms, "WMS", cancellationToken);
        var request = parameters.RequiredRequest();
        return request.ToUpperInvariant() switch
        {
            "GETCAPABILITIES" => await CapabilitiesAsync(map, services, options, context, cancellationToken),
            "GETMAP" => await GetMapAsync(map, parameters, services, cancellationToken),
            "GETFEATUREINFO" => await GetFeatureInfoAsync(map, parameters, services, cancellationToken),
            _ => throw OgcServiceException.NotSupported($"The WMS operation '{request}' is not supported."),
        };
    }

    private static async Task<IResult> CapabilitiesAsync(
        Map map, OgcRequestServices services, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        var layers = new List<OgcLayer>();
        foreach (var layer in OgcLayers.FeatureLayers(map))
        {
            layers.Add(await services.LoadAsync(map, layer, cancellationToken));
        }

        var baseUrl = BaseUrl(context, options, map.Name);
        var xml = await WmsCapabilities.BuildAsync(map, layers, baseUrl, services, options, cancellationToken);
        return Results.Text(xml, "application/xml");
    }

    private static async Task<IResult> GetMapAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var layers = OgcLayers.Select(map, parameters.List("layers"));
        var request = new MapRenderRequest(
            ParseViewport(parameters),
            MapStyle.Compose(map.Name, layers),
            OgcRender.Sources(services, map, layers),
            null,
            ParseFormat(parameters.Get("format")),
            90,
            ParseColor(parameters.Get("bgcolor")),
            ParseTransparent(parameters.Get("transparent")),
            1.0);
        var image = await services.Renderer.RenderAsync(request, cancellationToken);
        return Results.Bytes(image.Content, image.MediaType);
    }

    private static async Task<IResult> GetFeatureInfoAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var names = parameters.List("query_layers");
        var layers = OgcLayers.Select(map, names.Count > 0 ? names : parameters.List("layers"));
        var viewport = ParseViewport(parameters);
        var point = ClickPoint(parameters, viewport);
        var tolerance = viewport.UnitsPerPixel > 0 ? viewport.UnitsPerPixel / 2 : 1e-6;
        var matches = new List<(DatasetDescription Dataset, Feature Feature)>();
        foreach (var layer in layers)
        {
            var loaded = await services.LoadAsync(map, layer, cancellationToken);
            var features = await QueryNearAsync(services, loaded, viewport.Crs, point, tolerance, cancellationToken);
            matches.AddRange(features.Select(feature => (loaded.Description, feature)));
        }

        return WriteFeatureInfo(parameters.Get("info_format"), matches);
    }

    private static async Task<IReadOnlyList<Feature>> QueryNearAsync(
        OgcRequestServices services, OgcLayer layer, string viewportCrs, Coordinate point, double tolerance, CancellationToken cancellationToken)
    {
        var box = new Envelope(point.X - tolerance, point.Y - tolerance, point.X + tolerance, point.Y + tolerance);
        var projected = OgcGeometry.Transform(box, viewportCrs, Crs(layer.Description.Srid), services.Transforms, cancellationToken);
        var bbox = new BoundingBox(projected.MinX, projected.MinY, projected.MaxX, projected.MaxY);
        var batches = await services.Features(layer.Store).QueryAsync(layer.Layer.Dataset, bbox, null, cancellationToken);
        return batches.SelectMany(batch => batch.Features).ToArray();
    }

    private static IResult WriteFeatureInfo(string? infoFormat, IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> matches) =>
        infoFormat?.ToLowerInvariant() switch
        {
            null or "text/plain" => Results.Text(TextInfo(matches), "text/plain"),
            "application/json" or "application/geo+json" => Results.Bytes(GeoJson.FeatureCollection(matches), "application/json"),
            _ => throw OgcServiceException.NotSupported(
                $"Unsupported WMS info format '{infoFormat}'; supported: text/plain, application/json."),
        };

    private static string TextInfo(IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> matches)
    {
        var builder = new StringBuilder();
        foreach (var (dataset, feature) in matches)
        {
            builder.Append("Layer: ").Append(dataset.Id).AppendLine();
            builder.Append("Feature: ").Append(feature.Id.Value).AppendLine();
            for (var index = 0; index < feature.Schema.Count; index++)
            {
                if (feature.Schema[index].Kind == AttributeKind.Geometry)
                {
                    continue;
                }

                builder.Append("  ").Append(feature.Schema[index].Name).Append(" = ")
                    .Append(FormatValue(feature[index])).AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string FormatValue(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Boolean => value.BooleanValue ? "true" : "false",
        AttributeKind.Int64 => value.Int64Value.ToString(CultureInfo.InvariantCulture),
        AttributeKind.Double => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        AttributeKind.String => value.StringValue,
        AttributeKind.DateTimeOffset => value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
        AttributeKind.Guid => value.GuidValue.ToString(),
        _ => string.Empty,
    };

    private static RasterViewport ParseViewport(OgcParameters parameters)
    {
        var crs = parameters.Get("crs") ?? parameters.Get("srs") ?? throw OgcServiceException.Missing("crs");
        var (identity, yFirst) = OgcCrs.Resolve(crs);
        var bounds = OgcGeometry.ParseBbox(parameters.Required("bbox"));
        var xFirst = yFirst ? new Envelope(bounds.MinY, bounds.MinX, bounds.MaxY, bounds.MaxX) : bounds;
        var width = PositiveInt(parameters.Required("width"), "width");
        var height = PositiveInt(parameters.Required("height"), "height");
        return new RasterViewport(xFirst, width, height, identity);
    }

    private static Coordinate ClickPoint(OgcParameters parameters, RasterViewport viewport)
    {
        var i = OptionalNumber(parameters.Get("i") ?? parameters.Get("x"), "i");
        var j = OptionalNumber(parameters.Get("j") ?? parameters.Get("y"), "j");
        if (i is null || j is null)
        {
            return new Coordinate(viewport.Bounds.CenterX, viewport.Bounds.CenterY);
        }

        var x = viewport.Bounds.MinX + ((i.Value + 0.5) / viewport.Width) * viewport.Bounds.Width;
        var y = viewport.Bounds.MaxY - ((j.Value + 0.5) / viewport.Height) * viewport.Bounds.Height;
        return new Coordinate(x, y);
    }

    private static RasterFormat ParseFormat(string? format) => format?.ToUpperInvariant() switch
    {
        null or "IMAGE/PNG" or "PNG" => RasterFormat.Png,
        "IMAGE/JPEG" or "IMAGE/JPG" or "JPEG" or "JPG" => RasterFormat.Jpeg,
        _ => throw OgcServiceException.Invalid($"Unsupported WMS format '{format}'; supported: image/png, image/jpeg."),
    };

    private static bool ParseTransparent(string? value) => value?.ToUpperInvariant() switch
    {
        null or "FALSE" or "0" => false,
        "TRUE" or "1" => true,
        _ => throw OgcServiceException.Invalid($"The 'transparent' parameter must be TRUE or FALSE, got '{value}'."),
    };

    private static string? ParseColor(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        trimmed = trimmed.StartsWith('#')
            ? trimmed[1..]
            : trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        return trimmed.Length == 6 && trimmed.All(Uri.IsHexDigit)
            ? "#" + trimmed.ToUpperInvariant()
            : throw OgcServiceException.Invalid($"The 'bgcolor' parameter must be 0xRRGGBB or #RRGGBB, got '{value}'.");
    }

    private static int PositiveInt(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw OgcServiceException.Invalid($"The '{name}' parameter must be a positive integer, got '{text}'.");

    private static double? OptionalNumber(string? text, string name)
    {
        if (text is null)
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw OgcServiceException.Invalid($"The '{name}' parameter must be a number, got '{text}'.");
    }

    private static string Crs(int srid) => $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}";

    private static string BaseUrl(HttpContext context, OgcOptions options, string name) =>
        $"{context.Request.Scheme}://{context.Request.Host}{options.Root}/{name}/wms";
}
