using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer render bridge (spec §4.0.4/§4.1, ADR-0048): it turns a
/// publication's selected layers into a <see cref="MapRenderRequest"/> over
/// the SDK render contract, composing the persisted style with
/// <see cref="MapStyle.Compose"/> and pushing a supported <c>layerDefs</c>
/// where clause down as each layer's parameterised store filter (never as
/// SQL). Tile caching is content-addressed by the service and its style.
/// </summary>
internal static class MapRenderEngine
{
    /// <summary>
    /// Resolves each selected layer's keyed store and catalogue into render
    /// sources, carrying the layer's <c>layerDefs</c> filter and its export
    /// <c>time</c> extent (T-040) when the layer honours time.
    /// </summary>
    public static IReadOnlyList<MapLayerSource> Sources(
        IStoreRegistry stores, string store, IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, string>? layerDefs,
        IReadOnlyDictionary<int, MapTimeExtent>? times = null)
    {
        var features = stores.Features(store);
        var catalogue = stores.Catalogue(store);
        return layers
            .Select(layer => new MapLayerSource(
                layer.Dataset, features, catalogue, layerDefs?.GetValueOrDefault(layer.Id), times?.GetValueOrDefault(layer.Id)))
            .ToArray();
    }

    /// <summary>The map layers behind the resolved serving layers.</summary>
    public static IReadOnlyList<MapLayer> ToMapLayers(IReadOnlyList<PublishedLayer> layers) =>
        [.. layers.Select(layer => new MapLayer(layer.Dataset, layer.Id, layer.Name, layer.Style))];

    /// <summary>The MapLibre style document for the selected layers.</summary>
    public static string Style(string name, IReadOnlyList<PublishedLayer> layers) =>
        MapStyle.Compose(name, ToMapLayers(layers));

    /// <summary>A content version for the tile cache: the service and its composed style.</summary>
    public static string Version(string service, string style) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + style)));

    /// <summary>
    /// Parses a <c>layerDefs</c> JSON object (<c>{"0":"where"}</c>) into a
    /// per-layer store filter. Each clause is parsed with the shared safe
    /// grammar and re-rendered, so client text never becomes SQL structure.
    /// </summary>
    public static IReadOnlyDictionary<int, string>? ParseLayerDefs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var document = ParseDocument(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw GeoServicesErrors.Invalid(InvalidLayerDefs);
        }

        var defs = new Dictionary<int, string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (TryReadDefinition(property, out var id, out var where))
            {
                defs[id] = where;
            }
        }

        return defs.Count == 0 ? null : defs;
    }

    private const string InvalidLayerDefs = "'layerDefs' must be a JSON object of layer id to where clause.";

    private static JsonDocument ParseDocument(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw GeoServicesErrors.Invalid(InvalidLayerDefs);
        }
    }

    /// <summary>
    /// Reads one <c>{"id":"where"}</c> entry: a non-integer name or an
    /// unsupported clause is invalid, a blank clause is skipped.
    /// </summary>
    private static bool TryReadDefinition(JsonProperty property, out int id, out string where)
    {
        where = string.Empty;
        if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
        {
            throw GeoServicesErrors.Invalid($"'layerDefs' names layer '{property.Name}', which is not an integer id.");
        }

        if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
        {
            return false;
        }

        if (!EsriFilterClause.TryParse(property.Value.GetString()!, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid($"'layerDefs' clause for layer {id} is not supported: {error}.");
        }

        where = clause!.ToWhere();
        return true;
    }

    private static readonly Dictionary<string, RasterFormat> FormatByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = RasterFormat.Png,
        ["png"] = RasterFormat.Png,
        ["png8"] = RasterFormat.Png,
        ["png24"] = RasterFormat.Png,
        ["png32"] = RasterFormat.Png,
        ["jpg"] = RasterFormat.Jpeg,
        ["jpeg"] = RasterFormat.Jpeg,
        ["webp"] = RasterFormat.Webp,
        ["tif"] = RasterFormat.Tiff,
        ["tiff"] = RasterFormat.Tiff,
    };

    /// <summary>Parses the MapServer <c>format</c> parameter to an engine raster format.</summary>
    public static RasterFormat ParseFormat(string? format)
    {
        var key = format?.Trim() ?? string.Empty;
        return FormatByName.TryGetValue(key, out var parsed)
            ? parsed
            : throw GeoServicesErrors.Invalid($"Image format '{format}' is not supported (png, jpg, webp, tiff).");
    }

    /// <summary>Parses the <c>bbox</c> parameter (four comma-separated numbers).</summary>
    public static Envelope ParseBbox(string? value) =>
        Envelope(EsriValueParser.ParseDoubles(value ?? throw GeoServicesErrors.Invalid("The 'bbox' parameter is required."), "bbox"));

    /// <summary>Parses the <c>size</c> parameter (<c>width,height</c>).</summary>
    public static (int Width, int Height) ParseSize(string? value)
    {
        var values = EsriValueParser.ParseDoubles(value ?? throw GeoServicesErrors.Invalid("The 'size' parameter is required."), "size");
        if (values.Count < 2 || values[0] < 1 || values[1] < 1)
        {
            throw GeoServicesErrors.Invalid("'size' must be width,height with positive values.");
        }

        return ((int)values[0], (int)values[1]);
    }

    /// <summary>Parses the optional <c>dpi</c> parameter, defaulting to 96.</summary>
    public static double Dpi(string? value) =>
        string.IsNullOrWhiteSpace(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) || dpi <= 0
            ? 96
            : dpi;

    /// <summary>
    /// Reprojects an envelope between CRSs by transforming its bounding
    /// polygon, so a <c>bboxSR</c> different from the <c>imageSR</c> still
    /// frames the requested area.
    /// </summary>
    public static Envelope Project(Envelope envelope, CoordinateReference? from, CoordinateReference? to, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        if (envelope.IsEmpty || to is not { } target || from is not { } source || source == target)
        {
            return envelope;
        }

        var polygon = GeometryFactory.CreatePolygon(
            [
                new Coordinate(envelope.MinX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MinY),
            ],
            source);
        return transforms.Transform(polygon, source.ToString(), target.ToString(), cancellationToken).Envelope ?? envelope;
    }

    private static Envelope Envelope(IReadOnlyList<double> values) =>
        values.Count < 4
            ? throw GeoServicesErrors.Invalid("'bbox' must be xmin,ymin,xmax,ymax.")
            : new Envelope(values[0], values[1], values[2], values[3]);
}
