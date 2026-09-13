using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The OGC Web Map Service 1.3.0 projection (ADR-0053 §3): GetCapabilities,
/// GetMap, GetLegendGraphic and GetFeatureInfo over a map's feature layers. GetMap renders the
/// map's persisted style through <see cref="IMapRenderer"/> in the requested
/// CRS/bbox/size; GetFeatureInfo queries the selected layers through
/// <see cref="IFeatureStore.QueryAsync"/> near the clicked pixel. The
/// projection is read-only and never sees a protocol type outside this file.
/// </summary>
internal static class WmsService
{
    private static readonly Dictionary<AttributeKind, Func<AttributeValue, string>> ValueFormatters = new()
    {
        [AttributeKind.Boolean] = value => value.BooleanValue ? "true" : "false",
        [AttributeKind.Int64] = value => value.Int64Value.ToString(CultureInfo.InvariantCulture),
        [AttributeKind.Double] = value => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        [AttributeKind.String] = value => value.StringValue,
        [AttributeKind.DateTimeOffset] = value => value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
        [AttributeKind.Guid] = value => value.GuidValue.ToString(),
    };

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
            "GETLEGENDGRAPHIC" => await GetLegendGraphicAsync(map, parameters, services, cancellationToken),
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
        var exceptions = ExceptionsMode(parameters);
        try
        {
            return await RenderMapAsync(map, parameters, services, cancellationToken);
        }
        catch (Exception exception) when (exception is OgcServiceException or SpatialException && exceptions is not WmsExceptionsMode.Xml)
        {
            return await RenderErrorImageAsync(parameters, exceptions, services, cancellationToken);
        }
    }

    private static async Task<IResult> RenderMapAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        RequireDefaultStyles(parameters.List("styles"));
        var layers = OgcLayers.Select(map, parameters.List("layers"));
        var request = new MapRenderRequest(
            ParseViewport(parameters, requireVersion: true),
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

    /// <summary>
    /// The fixed legend frame: QGIS's GetLegendGraphic carries no size, so
    /// the legend renders at this frame unless WIDTH/HEIGHT override it.
    /// </summary>
    internal const int LegendWidth = 120;
    internal const int LegendHeight = 60;

    /// <summary>
    /// Renders one layer's persisted style over its own extent at legend
    /// size (research/interop/wms-conformance.md G6): QGIS's layer tree
    /// requests one PNG per visible layer, so the legend is a small
    /// single-layer render rather than a symbol swatch. STYLE accepts the
    /// advertised <c>default</c> only; an unknown layer is
    /// <c>LayerNotDefined</c>.
    /// </summary>
    /// <summary>
    /// The GetMap EXCEPTIONS behaviour (research/interop/wms-conformance.md
    /// G7): XML serves the ServiceExceptionReport (the default); INIMAGE and
    /// BLANK serve the requested image MIME instead. The error frame carries
    /// no data — INIMAGE honours the request's transparency and background
    /// while BLANK is transparent when TRANSPARENT is set — because painting
    /// exception text would need a text rasterizer the adapter must not own.
    /// An unknown EXCEPTIONS value stays lenient and serves XML.
    /// </summary>
    private enum WmsExceptionsMode
    {
        Xml,
        InImage,
        Blank,
    }

    private static WmsExceptionsMode ExceptionsMode(OgcParameters parameters) =>
        parameters.Get("exceptions")?.Trim().ToUpperInvariant() switch
        {
            null or "" or "XML" or "APPLICATION/VND.OGC.SE_XML" or "TEXT/XML" => WmsExceptionsMode.Xml,
            "INIMAGE" or "APPLICATION/VND.OGC.SE_INIMAGE" => WmsExceptionsMode.InImage,
            "BLANK" or "APPLICATION/VND.OGC.SE_BLANK" => WmsExceptionsMode.Blank,
            _ => WmsExceptionsMode.Xml,
        };

    private static async Task<IResult> RenderErrorImageAsync(
        OgcParameters parameters, WmsExceptionsMode mode, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var transparent = mode == WmsExceptionsMode.Blank
            ? ParseTransparentDefault(parameters.Get("transparent"), true)
            : ParseTransparent(parameters.Get("transparent"));
        // A background-only style: the style compiler rejects a document
        // with no layers, and painting through the renderer keeps the
        // adapter free of rasterizer types.
        var color = transparent ? "rgba(0,0,0,0)" : (OptionalColor(parameters.Get("bgcolor")) ?? "#FFFFFF");
        var style = $"{{\"layers\":[{{\"id\":\"wms-error\",\"type\":\"background\",\"paint\":{{\"background-color\":\"{color}\"}}}}]}}";
        var request = new MapRenderRequest(
            new RasterViewport(
                new Envelope(0, 0, 1, 1),
                OptionalSize(parameters.Get("width"), 256),
                OptionalSize(parameters.Get("height"), 256),
                "EPSG:4326"),
            style,
            Array.Empty<MapLayerSource>(),
            null,
            OptionalFormat(parameters.Get("format")),
            90,
            null,
            transparent,
            1.0);
        var image = await services.Renderer.RenderAsync(request, cancellationToken);
        return Results.Bytes(image.Content, image.MediaType);
    }

    private static bool ParseTransparentDefault(string? value, bool fallback) => value?.ToUpperInvariant() switch
    {
        null => fallback,
        "FALSE" or "0" => false,
        "TRUE" or "1" => true,
        _ => fallback,
    };

    private static string? OptionalColor(string? value)
    {
        try
        {
            return ParseColor(value);
        }
        catch (OgcServiceException)
        {
            return null;
        }
    }

    private static RasterFormat OptionalFormat(string? format)
    {
        try
        {
            return ParseFormat(format);
        }
        catch (OgcServiceException)
        {
            return RasterFormat.Png;
        }
    }

    private static int OptionalSize(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static async Task<IResult> GetLegendGraphicAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var names = parameters.List("layer");
        if (names.Count == 0)
        {
            throw OgcServiceException.Missing("layer");
        }

        if (names.Count > 1)
        {
            throw OgcServiceException.Invalid("The 'layer' parameter names a single layer.");
        }

        RequireDefaultStyles(parameters.List("style"));
        var layer = OgcLayers.Select(map, names).Single();
        var loaded = await services.LoadAsync(map, layer, cancellationToken);
        var viewport = await LegendViewportAsync(services, loaded, parameters, cancellationToken);
        var request = new MapRenderRequest(
            viewport,
            MapStyle.Compose(map.Name, new[] { layer }),
            OgcRender.Sources(services, map, new[] { layer }),
            null,
            ParseFormat(parameters.Get("format")),
            90,
            ParseColor(parameters.Get("bgcolor")),
            ParseTransparent(parameters.Get("transparent")),
            1.0);
        var image = await services.Renderer.RenderAsync(request, cancellationToken);
        return Results.Bytes(image.Content, image.MediaType);
    }

    private static async Task<RasterViewport> LegendViewportAsync(
        OgcRequestServices services, OgcLayer loaded, OgcParameters parameters, CancellationToken cancellationToken)
    {
        var width = LegendSize(parameters.Get("width"), LegendWidth);
        var height = LegendSize(parameters.Get("height"), LegendHeight);
        var extent = await OgcGeometry.ExtentAsync(
            services.Features(loaded.Store), loaded.Layer.Dataset, cancellationToken);
        if (extent is { } box && !box.IsEmpty && box.Width > 0 && box.Height > 0)
        {
            return new RasterViewport(box, width, height, Crs(loaded.Description.Srid));
        }

        return new RasterViewport(new Envelope(-180, -90, 180, 90), width, height, "EPSG:4326");
    }

    private static int LegendSize(string? text, int fallback) =>
        text is null ? fallback : PositiveInt(text, "width");

    private static async Task<IResult> GetFeatureInfoAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var queryNames = parameters.List("query_layers");
        var names = queryNames.Count > 0 ? queryNames : parameters.List("layers");
        RequireQueryable(map, names);
        var layers = OgcLayers.Select(map, names);
        var viewport = ParseViewport(parameters, requireVersion: false);
        var point = ClickPoint(parameters, viewport);
        var matches = new List<(DatasetDescription Dataset, Feature Feature)>();
        foreach (var layer in layers)
        {
            var loaded = await services.LoadAsync(map, layer, cancellationToken);
            var tolerance = ClickTolerance(viewport, layer);
            var features = await QueryNearAsync(services, loaded, viewport.Crs, point, tolerance, cancellationToken);
            matches.AddRange(features.Select(feature => (loaded.Description, feature)));
        }

        return WriteFeatureInfo(parameters.Get("info_format"), matches);
    }

    /// <summary>
    /// The identify tolerance in viewport units: the layer's rendered marker
    /// radius plus half a pixel for the integer click coordinate, scaled by the
    /// viewport's units-per-pixel. A layer without a circle marker (lines and
    /// polygons) falls back to the clicked pixel alone.
    /// </summary>
    private static double ClickTolerance(RasterViewport viewport, MapLayer layer) =>
        viewport.UnitsPerPixel > 0
            ? viewport.UnitsPerPixel * (OgcClickTolerance.MarkerRadiusPixels(layer.Style) + OgcClickTolerance.BasePixels)
            : 1e-6;

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
            "text/html" => Results.Text(HtmlInfo(matches), "text/html"),
            "text/xml" => Results.Text(XmlInfo(matches), "text/xml"),
            "application/vnd.ogc.gml" => Results.Text(GmlInfo(matches), "application/vnd.ogc.gml"),
            _ => throw OgcServiceException.InvalidFormat(
                $"Unsupported WMS info format '{infoFormat}'; supported: text/plain, text/html, text/xml, application/json, application/vnd.ogc.gml."),
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

    internal static string FormatValue(AttributeValue value) =>
        ValueFormatters.TryGetValue(value.Kind, out var format) ? format(value) : string.Empty;

    private static string HtmlInfo(IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> matches)
    {
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html><html><head><title>GetFeatureInfo</title></head><body>");
        foreach (var (dataset, feature) in matches)
        {
            builder.Append("<h1>Layer: ").Append(WebUtility.HtmlEncode(dataset.Id)).Append("</h1>");
            builder.Append("<h2>Feature: ").Append(WebUtility.HtmlEncode(feature.Id.Value)).Append("</h2><table>");
            for (var index = 0; index < feature.Schema.Count; index++)
            {
                if (feature.Schema[index].Kind == AttributeKind.Geometry)
                {
                    continue;
                }

                builder.Append("<tr><td>").Append(WebUtility.HtmlEncode(feature.Schema[index].Name))
                    .Append("</td><td>").Append(WebUtility.HtmlEncode(FormatValue(feature[index])))
                    .Append("</td></tr>");
            }

            builder.Append("</table>");
        }

        builder.Append("</body></html>");
        return builder.ToString();
    }

    private static string XmlInfo(IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> matches)
    {
        var root = new XElement("FeatureInfoResponse");
        foreach (var (dataset, feature) in matches)
        {
            var layer = new XElement("Layer", new XAttribute("name", dataset.Id));
            var current = new XElement("Feature", new XAttribute("id", feature.Id.Value));
            for (var index = 0; index < feature.Schema.Count; index++)
            {
                if (feature.Schema[index].Kind == AttributeKind.Geometry)
                {
                    continue;
                }

                current.Add(new XElement(
                    "Attribute",
                    new XAttribute("name", feature.Schema[index].Name),
                    new XAttribute("value", FormatValue(feature[index]))));
            }

            layer.Add(current);
            root.Add(layer);
        }

        return OgcXml.Write(new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    private static string GmlInfo(IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> matches)
    {
        var root = new XElement(
            OgcXml.Wfs + "FeatureCollection",
            new XAttribute(XNamespace.Xmlns + "gml", OgcXml.Gml.NamespaceName),
            new XAttribute("numberMatched", matches.Count.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("numberReturned", matches.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var (dataset, feature) in matches)
        {
            var member = new XElement(OgcXml.Wfs + "member");
            var current = new XElement(
                XName.Get(SafeName(dataset.Id)),
                new XAttribute(OgcXml.Gml + "id", $"{SafeName(dataset.Id)}.{feature.Id.Value}"));
            var geometryName = GeometryName(feature);
            for (var index = 0; index < feature.Schema.Count; index++)
            {
                if (feature.Schema[index].Kind == AttributeKind.Geometry)
                {
                    continue;
                }

                current.Add(new XElement(XName.Get(SafeName(feature.Schema[index].Name)), FormatValue(feature[index])));
            }

            var geometryIndex = GeometryIndex(feature);
            if (geometryIndex >= 0 && !feature[geometryIndex].IsNull)
            {
                current.Add(new XElement(XName.Get(SafeName(geometryName)), WriteGmlGeometry(feature[geometryIndex].GeometryValue)));
            }

            member.Add(current);
            root.Add(member);
        }

        return OgcXml.Write(new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    private static int GeometryIndex(Feature feature)
    {
        for (var index = 0; index < feature.Schema.Count; index++)
        {
            if (feature.Schema[index].Kind == AttributeKind.Geometry)
            {
                return index;
            }
        }

        return -1;
    }

    private static string GeometryName(Feature feature)
    {
        var index = GeometryIndex(feature);
        return index >= 0 ? feature.Schema[index].Name : "geometry";
    }

    private static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var rune = name[index];
            var ok = index == 0
                ? char.IsLetter(rune) || rune == '_'
                : char.IsLetterOrDigit(rune) || rune is '.' or '-' or '_' or ':';
            builder.Append(ok ? rune : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static XElement WriteGmlGeometry(IGeometry geometry) => geometry switch
    {
        Point point => new XElement(
            OgcXml.Gml + "Point",
            point.Coordinate is { } coordinate
                ? new XElement(OgcXml.Gml + "pos", Doubles(coordinate.X, coordinate.Y))
                : null),
        MultiPoint multi => new XElement(
            OgcXml.Gml + "MultiPoint",
            multi.Points.Select(member => new XElement(OgcXml.Gml + "pointMember", WriteGmlGeometry(member)))),
        LineString line => new XElement(OgcXml.Gml + "LineString", new XElement(OgcXml.Gml + "posList", Positions(line.Sequence))),
        MultiLineString multi => new XElement(
            OgcXml.Gml + "MultiCurve",
            multi.LineStrings.Select(member => new XElement(OgcXml.Gml + "curveMember", WriteGmlGeometry(member)))),
        Polygon polygon => WriteGmlPolygon(polygon),
        MultiPolygon multi => new XElement(
            OgcXml.Gml + "MultiSurface",
            multi.Polygons.Select(member => new XElement(OgcXml.Gml + "surfaceMember", WriteGmlGeometry(member)))),
        GeometryCollection collection => new XElement(
            OgcXml.Gml + "MultiGeometry",
            collection.Geometries.Select(member => new XElement(OgcXml.Gml + "geometryMember", WriteGmlGeometry(member)))),
        _ => new XElement(OgcXml.Gml + "Point"),
    };

    private static XElement WriteGmlPolygon(Polygon polygon)
    {
        var current = new XElement(
            OgcXml.Gml + "Polygon",
            new XElement(
                OgcXml.Gml + "exterior",
                new XElement(OgcXml.Gml + "LinearRing", new XElement(OgcXml.Gml + "posList", Positions(polygon.ExteriorRing.Sequence)))));
        foreach (var ring in polygon.InteriorRings)
        {
            current.Add(new XElement(
                OgcXml.Gml + "interior",
                new XElement(OgcXml.Gml + "LinearRing", new XElement(OgcXml.Gml + "posList", Positions(ring.Sequence)))));
        }

        return current;
    }

    private static string Doubles(double x, double y) =>
        $"{x.ToString("R", CultureInfo.InvariantCulture)} {y.ToString("R", CultureInfo.InvariantCulture)}";

    private static string Positions(ICoordinateSequence sequence)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < sequence.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(sequence.GetOrdinate(index, Ordinate.X).ToString("R", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(sequence.GetOrdinate(index, Ordinate.Y).ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static RasterViewport ParseViewport(OgcParameters parameters, bool requireVersion)
    {
        var version = parameters.Get("version");
        if (requireVersion && version is null)
        {
            throw OgcServiceException.Missing("version");
        }

        var crs = parameters.Get("crs") ?? parameters.Get("srs") ?? throw OgcServiceException.Missing("crs");
        var (identity, yFirst) = OgcCrs.Resolve(crs, version);
        var bounds = OgcGeometry.ParseBbox(parameters.Required("bbox"));
        var xFirst = yFirst ? new Envelope(bounds.MinY, bounds.MinX, bounds.MaxY, bounds.MaxX) : bounds;
        RequireNonEmptyExtent(parameters.Get("bbox"), xFirst);
        var width = PositiveInt(parameters.Required("width"), "width");
        var height = PositiveInt(parameters.Required("height"), "height");
        return new RasterViewport(xFirst, width, height, identity);
    }

    /// <summary>
    /// Rejects a degenerate bbox: the envelope constructor already rejects an
    /// inverted extent, but a zero-width or zero-height extent (CITE
    /// <c>bbox-minx-eq-maxx</c> et al.) constructs fine and must raise a
    /// Service Exception instead of rendering.
    /// </summary>
    private static void RequireNonEmptyExtent(string? text, Envelope bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw OgcServiceException.Invalid($"The 'bbox' parameter must span a non-empty extent, got '{text}'.");
        }
    }

    private static Coordinate ClickPoint(OgcParameters parameters, RasterViewport viewport)
    {
        var i = OptionalPointNumber(parameters.Get("i") ?? parameters.Get("x"), "i");
        var j = OptionalPointNumber(parameters.Get("j") ?? parameters.Get("y"), "j");
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
        _ => throw OgcServiceException.InvalidFormat($"Unsupported WMS format '{format}'; supported: image/png, image/jpeg."),
    };

    private static bool ParseTransparent(string? value) => value?.ToUpperInvariant() switch
    {
        null or "FALSE" or "0" => false,
        "TRUE" or "1" => true,
        _ => throw OgcServiceException.Invalid($"The 'transparent' parameter must be TRUE or FALSE, got '{value}'."),
    };

    internal static string? ParseColor(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var digits = TrimColorPrefix(value.Trim());
        return IsHexColor(digits)
            ? $"#{digits.ToUpperInvariant()}"
            : throw OgcServiceException.Invalid($"The 'bgcolor' parameter must be 0xRRGGBB or #RRGGBB, got '{value}'.");
    }

    private static string TrimColorPrefix(string value)
    {
        if (value.StartsWith('#'))
        {
            return value[1..];
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    }

    private static bool IsHexColor(string digits) => digits.Length == 6 && digits.All(Uri.IsHexDigit);

    private static int PositiveInt(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw OgcServiceException.Invalid($"The '{name}' parameter must be a positive integer, got '{text}'.");

    private static double? OptionalPointNumber(string? text, string name)
    {
        if (text is null)
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw OgcServiceException.InvalidPoint($"The '{name}' parameter must be a number, got '{text}'.");
    }

    /// <summary>
    /// Rejects a named style: the service serves the persisted default style
    /// only (advertised as <c>default</c> in capabilities), so any other
    /// requested name is <c>StyleNotDefined</c>. Empty entries
    /// (<c>STYLES=</c>) select the default and are accepted; an all-default
    /// selection stays lenient on the style count, so a client sending one
    /// empty STYLES over several layers (QGIS) still renders.
    /// </summary>
    private static void RequireDefaultStyles(IReadOnlyList<string> styles)
    {
        foreach (var style in styles)
        {
            if (!string.IsNullOrWhiteSpace(style)
                && !string.Equals(style.Trim(), "default", StringComparison.OrdinalIgnoreCase))
            {
                throw OgcServiceException.StyleNotDefined(
                    $"Style '{style}' is not defined; this service serves the default style only.");
            }
        }
    }

    /// <summary>
    /// Rejects a GetFeatureInfo layer that exists on the map but is not a
    /// queryable feature layer (for example a raster image layer) with
    /// <c>LayerNotQueryable</c>. Unknown names fall through to
    /// <see cref="OgcLayers.Select"/> which reports <c>LayerNotDefined</c>.
    /// </summary>
    private static void RequireQueryable(Map map, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var layer = map.Layers.FirstOrDefault(candidate =>
                string.Equals(OgcLayers.NameOf(candidate), name, StringComparison.OrdinalIgnoreCase));
            if (layer is not null && layer.Kind != MapLayerKind.Feature)
            {
                throw OgcServiceException.LayerNotQueryable($"Layer '{name}' is not queryable.");
            }
        }
    }

    private static string Crs(int srid) => $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}";

    private static string BaseUrl(HttpContext context, OgcOptions options, string name) =>
        $"{context.Request.Scheme}://{context.Request.Host}{options.Root}/{name}/wms";
}
