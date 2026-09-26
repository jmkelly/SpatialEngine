using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The serving seams every WMS resource needs: the resolved map, the store
/// and renderer services behind it, the OGC options and the request's
/// cancellation. Grouping them keeps each operation's signature to the
/// operation itself.
/// </summary>
internal sealed record WmsServing(
    Map Map, OgcRequestServices Services, OgcOptions Options, HttpContext Context, CancellationToken CancellationToken);

/// <summary>
/// The OGC Web Map Service projection (ADR-0053 §3): GetCapabilities (1.3.0
/// and 1.1.1 dialects), GetMap, GetLegendGraphic and GetFeatureInfo over a
/// map's feature layers. GetMap renders the
/// map's persisted style through <see cref="IMapRenderer"/> in the requested
/// CRS/bbox/size; GetFeatureInfo queries the selected layers through
/// <see cref="IFeatureStore.QueryAsync"/> near the clicked pixel. SLD style
/// overrides (GetStyles, DescribeLayer, SLD/SLD_BODY) are rejected: the
/// service renders the persisted default style only (T-045 diagnostics
/// verdict — no recorded client trace sends them). The
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
        var map = await services.ResolveMapAsync(name, MapServiceKind.Wms, "WMS", cancellationToken);
        var request = parameters.RequiredRequest().ToUpperInvariant();
        return await DispatchAsync(
            request, new WmsServing(map, services, options, context, cancellationToken), parameters);
    }

    /// <summary>The served WMS operations; the table is the dispatch, an unknown request is <c>OperationNotSupported</c>.</summary>
    private static Task<IResult> DispatchAsync(string request, WmsServing serving, OgcParameters parameters) =>
        Operations.TryGetValue(request, out var operation)
            ? operation(serving, parameters)
            : throw OgcServiceException.NotSupported($"The WMS operation '{request}' is not supported.");

    private static readonly Dictionary<string, WmsOperation> Operations = new(StringComparer.Ordinal)
    {
        ["GETCAPABILITIES"] = (serving, parameters) => CapabilitiesAsync(serving, parameters),
        ["GETMAP"] = (serving, parameters) =>
            GetMapAsync(serving.Map, parameters, serving.Services, serving.CancellationToken),
        ["GETFEATUREINFO"] = (serving, parameters) =>
            GetFeatureInfoAsync(serving.Map, parameters, serving.Services, serving.CancellationToken),
        ["GETLEGENDGRAPHIC"] = (serving, parameters) =>
            GetLegendGraphicAsync(serving.Map, parameters, serving.Services, serving.CancellationToken),
    };

    /// <summary>One served WMS operation, uniform so the dispatch table can hold it.</summary>
    private delegate Task<IResult> WmsOperation(WmsServing serving, OgcParameters parameters);

    private static async Task<IResult> CapabilitiesAsync(WmsServing serving, OgcParameters parameters)
    {
        var version = NegotiateCapabilitiesVersion(parameters.Get("version"));
        var layers = new List<OgcLayer>();
        foreach (var layer in OgcLayers.FeatureLayers(serving.Map))
        {
            layers.Add(await serving.Services.LoadAsync(serving.Map, layer, serving.CancellationToken));
        }

        var baseUrl = BaseUrl(serving);
        var xml = await WmsCapabilities.BuildAsync(serving, layers, baseUrl, version);
        return Results.Text(xml, "application/xml");
    }

    /// <summary>
    /// Selects the capabilities dialect (T-045 item 2): no VERSION (what QGIS
    /// sends on add-layer) and 1.3.x serve the 1.3.0 dialect; 1.1.x serves
    /// the 1.1.1 dialect (SRS vocabulary, LatLonBoundingBox); anything else
    /// is <c>InvalidParameterValue</c>.
    /// </summary>
    private static string NegotiateCapabilitiesVersion(string? version)
    {
        if (version is null)
        {
            return "1.3.0";
        }

        var trimmed = version.Trim();
        if (trimmed.StartsWith("1.3", StringComparison.Ordinal))
        {
            return "1.3.0";
        }

        if (trimmed.StartsWith("1.1", StringComparison.Ordinal))
        {
            return "1.1.1";
        }

        throw OgcServiceException.Invalid(
            $"Unsupported WMS version '{version}'; supported versions are 1.3.0 and 1.1.1.");
    }

    private static async Task<IResult> GetMapAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var exceptions = ExceptionsMode(parameters);
        try
        {
            return await RenderMapAsync(map, parameters, services, cancellationToken);
        }
        catch (Exception exception) when (IsRenderedFailure(exception, exceptions))
        {
            return await RenderErrorImageAsync(parameters, exceptions, services, cancellationToken);
        }
    }

    /// <summary>Only a mapped failure renders as an image, and only when EXCEPTIONS asked for one.</summary>
    private static bool IsRenderedFailure(Exception exception, WmsExceptionsMode mode) =>
        KnownFailure(exception) && mode is not WmsExceptionsMode.Xml;

    private static bool KnownFailure(Exception exception) => exception is OgcServiceException or SpatialException;

    private static async Task<IResult> RenderMapAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        RequireDefaultStyles(parameters.List("styles"));
        RequireNoSld(parameters);
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
            1.0,
            ParseDpi(parameters));
        var image = await services.Renderer.RenderAsync(request, cancellationToken);
        return Results.Bytes(image.Content, image.MediaType);
    }

    /// <summary>
    /// Rejects an SLD style override (T-045 item 1): the service renders the
    /// persisted default style only, so a client-supplied SLD/SLD_BODY must
    /// fail loudly instead of rendering the wrong style with a 200. No
    /// recorded client trace has ever sent these parameters; GetStyles and
    /// DescribeLayer keep the default-arm OperationNotSupported reject.
    /// </summary>
    private static void RequireNoSld(OgcParameters parameters)
    {
        var source = parameters.Get("sld") is not null
            ? "sld"
            : parameters.Get("sld_body") is not null ? "sld_body" : null;
        if (source is not null)
        {
            throw OgcServiceException.NotSupported(
                $"The '{source}' parameter is not supported; this service renders the persisted default style only.");
        }
    }

    /// <summary>
    /// Reads the QGIS dpiMode=7 triple (T-045 item 3): DPI wins, then
    /// MAP_RESOLUTION (MapServer), then FORMAT_OPTIONS dpi:N (GeoServer).
    /// Absent means the style reference DPI the renderer defines
    /// (<see cref="MapRenderRequest.ReferenceDpi"/>); a present-but-malformed
    /// DPI or MAP_RESOLUTION is InvalidParameterValue, while a malformed dpi
    /// inside the multi-value FORMAT_OPTIONS bag is skipped.
    /// </summary>
    internal static double ParseDpi(OgcParameters parameters)
    {
        var dpi = parameters.Get("dpi");
        if (dpi is not null)
        {
            return RequireDpi(dpi, "dpi");
        }

        var resolution = parameters.Get("map_resolution");
        if (resolution is not null)
        {
            return RequireDpi(resolution, "map_resolution");
        }

        var options = parameters.Get("format_options");
        if (options is not null && TryFormatOptionsDpi(options, out var value))
        {
            return value;
        }

        return MapRenderRequest.ReferenceDpi;
    }

    private static double RequireDpi(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) && value > 0
            ? value
            : throw OgcServiceException.Invalid($"The '{name}' parameter must be a positive number, got '{text}'.");

    private static bool TryFormatOptionsDpi(string options, out double value)
    {
        value = MapRenderRequest.ReferenceDpi;
        foreach (var token in options.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryTokenDpi(token, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryTokenDpi(string token, out double value)
    {
        value = MapRenderRequest.ReferenceDpi;
        var pair = token.Split(':', 2, StringSplitOptions.TrimEntries);
        return pair.Length == 2 && IsDpiPair(pair[0], pair[1], out value);
    }

    private static bool IsDpiPair(string name, string text, out double value)
    {
        value = MapRenderRequest.ReferenceDpi;
        return string.Equals(name, "dpi", StringComparison.OrdinalIgnoreCase)
            && TryPositiveDpi(text, out value);
    }

    private static bool TryPositiveDpi(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value) && value > 0;

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

    /// <summary>The EXCEPTIONS values the spec names, upper-cased; an unknown value stays XML (lenient).</summary>
    private static readonly Dictionary<string, WmsExceptionsMode> ExceptionModes = new(StringComparer.Ordinal)
    {
        [""] = WmsExceptionsMode.Xml,
        ["XML"] = WmsExceptionsMode.Xml,
        ["APPLICATION/VND.OGC.SE_XML"] = WmsExceptionsMode.Xml,
        ["TEXT/XML"] = WmsExceptionsMode.Xml,
        ["INIMAGE"] = WmsExceptionsMode.InImage,
        ["APPLICATION/VND.OGC.SE_INIMAGE"] = WmsExceptionsMode.InImage,
        ["BLANK"] = WmsExceptionsMode.Blank,
        ["APPLICATION/VND.OGC.SE_BLANK"] = WmsExceptionsMode.Blank,
    };

    private static WmsExceptionsMode ExceptionsMode(OgcParameters parameters) =>
        ExceptionModes.TryGetValue(parameters.Get("exceptions")?.Trim().ToUpperInvariant() ?? string.Empty, out var mode)
            ? mode
            : WmsExceptionsMode.Xml;

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

    private static bool ParseTransparentDefault(string? value, bool fallback) =>
        TransparentFlags.TryGetValue(value?.ToUpperInvariant() ?? string.Empty, out var flag) ? flag : fallback;

    /// <summary>The TRANSPARENT values the spec names, upper-cased; anything else keeps the caller's fallback.</summary>
    private static readonly Dictionary<string, bool> TransparentFlags = new(StringComparer.Ordinal)
    {
        ["FALSE"] = false,
        ["0"] = false,
        ["TRUE"] = true,
        ["1"] = true,
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
        PositiveSize(text, out var value) ? value : fallback;

    private static bool PositiveSize(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;

    private static async Task<IResult> GetLegendGraphicAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var names = RequireSingleLayer(parameters);
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
            1.0,
            ParseDpi(parameters));
        var image = await services.Renderer.RenderAsync(request, cancellationToken);
        return Results.Bytes(image.Content, image.MediaType);
    }

    /// <summary>GetLegendGraphic renders exactly one layer: none is <c>Missing</c>, several is <c>Invalid</c>.</summary>
    private static IReadOnlyList<string> RequireSingleLayer(OgcParameters parameters)
    {
        var names = parameters.List("layer");
        return names.Count == 1 ? names : throw SingleLayerProblem(names.Count);
    }

    private static OgcServiceException SingleLayerProblem(int count) =>
        count == 0
            ? OgcServiceException.Missing("layer")
            : OgcServiceException.Invalid("The 'layer' parameter names a single layer.");

    private static async Task<RasterViewport> LegendViewportAsync(
        OgcRequestServices services, OgcLayer loaded, OgcParameters parameters, CancellationToken cancellationToken)
    {
        var extent = await OgcGeometry.ExtentAsync(
            services.Features(loaded.Store), loaded.Layer.Dataset, cancellationToken);
        return Drawable(extent)
            ? new RasterViewport(extent!.Value, LegendSize(parameters, "width", LegendWidth), LegendSize(parameters, "height", LegendHeight), Crs(loaded.Description.Srid))
            : new RasterViewport(new Envelope(-180, -90, 180, 90), LegendSize(parameters, "width", LegendWidth), LegendSize(parameters, "height", LegendHeight), "EPSG:4326");
    }

    /// <summary>A layer's own extent renders only when the store reported a non-degenerate box.</summary>
    private static bool Drawable(Envelope? extent) =>
        extent is { } box && HasArea(box);

    private static bool HasArea(Envelope box) => Positive(box.Width) && Positive(box.Height);

    private static bool Positive(double span) => span > 0;

    private static int LegendSize(OgcParameters parameters, string name, int fallback)
    {
        var text = parameters.Get(name);
        return text is null ? fallback : PositiveInt(text, name);
    }

    private static async Task<IResult> GetFeatureInfoAsync(
        Map map, OgcParameters parameters, OgcRequestServices services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queryNames = parameters.List("query_layers");
        var names = queryNames.Count > 0 ? queryNames : parameters.List("layers");
        RequireQueryable(map, names);
        var layers = OgcLayers.Select(map, names);
        var viewport = ParseViewport(parameters, requireVersion: false);
        var point = ClickPoint(parameters, viewport);
        var limit = OptionalFeatureCount(parameters.Get("feature_count"));
        var matches = new List<(DatasetDescription Dataset, Feature Feature)>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await services.LoadAsync(map, layer, cancellationToken);
            var tolerance = ClickTolerance(viewport, layer);
            var features = await QueryNearAsync(services, loaded, viewport.Crs, point, tolerance, cancellationToken);
            matches.AddRange(features.Select(feature => (loaded.Description, feature)));
            if (limit is not null && matches.Count >= limit.Value)
            {
                break;
            }
        }

        return WriteFeatureInfo(
            parameters.Get("info_format"),
            limit is null ? matches : matches.Take(limit.Value).ToList());
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
                current.Add(new XElement(XName.Get(SafeName(geometryName)), WmsGmlWriter.WriteGmlGeometry(feature[geometryIndex].GeometryValue)));
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

    /// <summary>
    /// Reads the GetFeatureInfo row cap (research/interop/wms-conformance.md
    /// G8): QGIS sends <c>FEATURE_COUNT</c> (default 10) and the MapServer
    /// traces use <c>FEATURE_COUNT=5</c>. Absent means every match (the
    /// previous behaviour); a present value must be a positive integer.
    /// </summary>
    private static int? OptionalFeatureCount(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw OgcServiceException.Invalid($"The 'feature_count' parameter must be a positive integer, got '{text}'.");
    }

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
            RequireDefaultStyle(style);
        }
    }

    private static void RequireDefaultStyle(string style)
    {
        if (IsNamedStyle(style))
        {
            throw OgcServiceException.StyleNotDefined(
                $"Style '{style}' is not defined; this service serves the default style only.");
        }
    }

    /// <summary>A named style is one the service does not serve: an empty entry selects the default.</summary>
    private static bool IsNamedStyle(string style) =>
        !string.IsNullOrWhiteSpace(style) && !string.Equals(style.Trim(), "default", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The public URL a client calls, and the one the capabilities advertise.</summary>
    private static string BaseUrl(WmsServing serving) =>
        $"{serving.Context.Request.Scheme}://{serving.Context.Request.Host}{serving.Options.Root}/{serving.Map.Name}/wms";
}
