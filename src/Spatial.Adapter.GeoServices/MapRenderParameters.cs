using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The image-render request grammar shared by the MapServer export route
/// (spec §4.0.4), the tile routes and the ImageServer export path: the
/// <c>layerDefs</c> per-layer filter map, the raster <c>format</c>, the
/// <c>bbox</c>/<c>size</c> frame and the <c>dpi</c> resolution. Split out of
/// <see cref="MapRenderEngine"/> — which builds the render request itself —
/// so parsing an Esri parameter and composing a render request carry their
/// own fan-out. Every parse is typed: an unsupported value is
/// <c>invalid.arguments</c> naming the parameter, never a silent default
/// beyond the documented one.
/// </summary>
internal static class MapRenderParameters
{
    private const string InvalidLayerDefs = "'layerDefs' must be a JSON object of layer id to where clause.";

    /// <summary>
    /// Parses a <c>layerDefs</c> JSON object (<c>{"0":"where"}</c>) into a
    /// per-layer where clause compiled to the engine's one predicate
    /// vocabulary (ADR-0074), so client text never becomes SQL structure and
    /// the clause reaches the store as a plan rather than as a string.
    /// </summary>
    public static IReadOnlyDictionary<int, EsriWhere>? ParseLayerDefs(string? value)
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

        var defs = new Dictionary<int, EsriWhere>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (TryReadDefinition(property, out var id, out var where))
            {
                defs[id] = where;
            }
        }

        return defs.Count == 0 ? null : defs;
    }

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
    private static bool TryReadDefinition(JsonProperty property, out int id, out EsriWhere where)
    {
        where = EsriWhere.None;
        if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
        {
            throw GeoServicesErrors.Invalid($"'layerDefs' names layer '{property.Name}', which is not an integer id.");
        }

        if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
        {
            return false;
        }

        if (!EsriWhere.TryParse(property.Value.GetString()!, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid($"'layerDefs' clause for layer {id} is not supported: {error}.");
        }

        where = clause!;
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

    /// <summary>
    /// Resizes an export extent to the image aspect (spec §export: "the
    /// extent should be resized to prevent map images from appearing
    /// stretched"). The shorter side grows, centered on the requested
    /// bounds, so the answer covers the whole bbox at a uniform scale; a
    /// matching frame returns unchanged. Render framing, not a spatial
    /// algorithm, so it lives beside the bbox/size grammar rather than in
    /// <c>Spatial.Core</c>.
    /// </summary>
    public static Envelope FitExtent(Envelope bounds, int width, int height)
    {
        var aspect = (double)width / height;
        if (bounds.Width <= 0 || bounds.Height <= 0
            || Math.Abs(bounds.Width / bounds.Height - aspect) <= 1e-9 * aspect)
        {
            return bounds;
        }

        if (bounds.Width / bounds.Height > aspect)
        {
            var half = bounds.Width / aspect / 2;
            return new Envelope(bounds.MinX, bounds.CenterY - half, bounds.MaxX, bounds.CenterY + half);
        }

        var side = bounds.Height * aspect / 2;
        return new Envelope(bounds.CenterX - side, bounds.MinY, bounds.CenterX + side, bounds.MaxY);
    }

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

    private static Envelope Envelope(IReadOnlyList<double> values) =>
        values.Count < 4
            ? throw GeoServicesErrors.Invalid("'bbox' must be xmin,ymin,xmax,ymax.")
            : new Envelope(values[0], values[1], values[2], values[3]);
}
