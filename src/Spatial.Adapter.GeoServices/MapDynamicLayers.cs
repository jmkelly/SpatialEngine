using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The export <c>dynamicLayers</c> parameter (S1 dynamic-layer-table/, S2
/// export-map/, T-040): per-request redefinition of the rendered layers.
/// Each entry rebinds a published layer by <c>source.mapLayerId</c> and may
/// override its renderer through <c>drawingInfo</c>; the override is
/// converted back into a MapLibre fragment (the inverse of the
/// <see cref="MapStyleProjection"/> subset) so the render pipeline needs no
/// second style model. Anything the engine cannot honour — new data sources
/// (joins, table queries, rasters), picture/text symbols, label overrides,
/// non-zero transparency — is a typed <c>invalid.arguments</c>, never a
/// silent fallback. Entry <c>name</c>/<c>minScale</c>/<c>maxScale</c> are
/// accepted and ignored: the engine performs no scale enforcement anywhere
/// (research §2), so a scale range cannot change the render.
/// </summary>
internal static class MapDynamicLayers
{
    /// <summary>Parses the <c>dynamicLayers</c> JSON array, or null when absent.</summary>
    public static IReadOnlyList<DynamicLayerOverride>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var document = ParseDocument(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' must be a JSON array of dynamic layer definitions.");
        }

        var overrides = new List<DynamicLayerOverride>();
        var ids = new HashSet<int>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var parsed = ParseEntry(element);
            if (!ids.Add(parsed.Id))
            {
                throw EsriInteropException.Invalid($"'dynamicLayers' names layer {parsed.Id} more than once.");
            }

            overrides.Add(parsed);
        }

        return overrides.Count == 0 ? null : overrides;
    }

    /// <summary>
    /// Rebinds the published layers: an override whose id matches a
    /// published layer replaces its dataset and style; a new id appends a
    /// layer. An unknown <c>mapLayerId</c> is a typed <c>not.found</c>,
    /// matching the service's unknown-layer behaviour.
    /// </summary>
    public static IReadOnlyList<PublishedLayer> Apply(
        IReadOnlyList<PublishedLayer> published, IReadOnlyList<DynamicLayerOverride>? overrides, string service)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return published;
        }

        var byDataset = published.ToDictionary(layer => layer.Id);
        var effective = published.ToList();
        foreach (var dynamic in overrides)
        {
            if (!byDataset.TryGetValue(dynamic.MapLayerId, out var source))
            {
                throw new EsriInteropException(
                    EsriErrorCodes.NotFound,
                    $"Layer {dynamic.MapLayerId} does not exist in service '{service}'.");
            }

            var rebound = new PublishedLayer(
                dynamic.Id,
                source.Dataset,
                string.IsNullOrWhiteSpace(dynamic.Name) ? source.Name : dynamic.Name.Trim(),
                dynamic.StyleOverride ?? source.Style);
            var index = effective.FindIndex(layer => layer.Id == dynamic.Id);
            if (index < 0)
            {
                effective.Add(rebound);
            }
            else
            {
                effective[index] = rebound;
            }
        }

        return effective;
    }

    private static JsonDocument ParseDocument(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' must be a JSON array of dynamic layer definitions.");
        }
    }

    private static DynamicLayerOverride ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' entries must be objects with an integer 'id' and a 'source'.");
        }

        var id = Integer(element, "id", "'dynamicLayers' entries must carry an integer 'id'.");
        var name = element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        var mapLayerId = ParseSource(element);
        var style = ParseDrawingInfo(element);
        return new DynamicLayerOverride(id, mapLayerId, name, style);
    }

    private static int ParseSource(JsonElement element)
    {
        if (!element.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' entries must carry a 'source' object.");
        }

        var type = source.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (!string.Equals(type, "mapLayer", StringComparison.OrdinalIgnoreCase))
        {
            throw EsriInteropException.Invalid(
                $"'dynamicLayers' source type '{type ?? "missing"}' is not supported: only an existing layer ('mapLayer' with 'mapLayerId') can be rebound; joins, table queries and raster data sources have no engine model.");
        }

        return Integer(source, "mapLayerId", "'dynamicLayers' mapLayer sources must carry an integer 'mapLayerId'.");
    }

    private static string? ParseDrawingInfo(JsonElement element)
    {
        if (!element.TryGetProperty("drawingInfo", out var drawing)
            || drawing.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (drawing.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' entry 'drawingInfo' must be an object with a 'renderer'.");
        }

        if (drawing.TryGetProperty("transparency", out var transparency)
            && transparency.ValueKind == JsonValueKind.Number
            && transparency.GetDouble() != 0)
        {
            throw EsriInteropException.Invalid(
                "'dynamicLayers' entry 'transparency' is not supported: the engine has no per-layer opacity model.");
        }

        if (drawing.TryGetProperty("labelingInfo", out var labels)
            && labels.ValueKind == JsonValueKind.Array
            && labels.GetArrayLength() > 0)
        {
            throw EsriInteropException.Invalid(
                "'dynamicLayers' entry 'labelingInfo' is not supported: labels come from the published style and cannot be overridden per request.");
        }

        if (!drawing.TryGetProperty("renderer", out var renderer) || renderer.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' entry 'drawingInfo' must carry a 'renderer' object.");
        }

        return RenderStyle(renderer);
    }

    private static string RenderStyle(JsonElement renderer)
    {
        var type = renderer.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        return type switch
        {
            "simple" => RenderSimple(renderer),
            "uniqueValue" => RenderUniqueValue(renderer),
            "classBreaks" => RenderClassBreaks(renderer),
            _ => throw EsriInteropException.Invalid(
                $"'dynamicLayers' renderer type '{type ?? "missing"}' is not supported (simple, uniqueValue, classBreaks)."),
        };
    }

    private static string RenderSimple(JsonElement renderer)
    {
        var symbol = RequireSymbol(renderer);
        return new JsonArray { Fragment(symbol, null) }.ToJsonString();
    }

    private static string RenderUniqueValue(JsonElement renderer)
    {
        var field = RequiredString(renderer, "field1", "'dynamicLayers' uniqueValue renderers must name 'field1'.");
        foreach (var extra in new[] { "field2", "field3" })
        {
            if (renderer.TryGetProperty(extra, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                throw EsriInteropException.Invalid(
                    "'dynamicLayers' uniqueValue renderers over more than one field are not supported.");
            }
        }

        if (!renderer.TryGetProperty("uniqueValueInfos", out var infos)
            || infos.ValueKind != JsonValueKind.Array
            || infos.GetArrayLength() == 0)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' uniqueValue renderers must carry a non-empty 'uniqueValueInfos'.");
        }

        var fragments = new JsonArray();
        if (renderer.TryGetProperty("defaultSymbol", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
        {
            fragments.Add(Fragment(fallback, null));
        }

        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("value", out var value)
                || (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Number && value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                || !info.TryGetProperty("symbol", out var symbol)
                || symbol.ValueKind != JsonValueKind.Object)
            {
                throw EsriInteropException.Invalid("'dynamicLayers' 'uniqueValueInfos' entries must carry a scalar 'value' and a 'symbol' object.");
            }

            var filter = new JsonArray { "==", field, JsonNode.Parse(value.GetRawText()) };
            fragments.Add(Fragment(symbol, filter));
        }

        return fragments.ToJsonString();
    }

    private static string RenderClassBreaks(JsonElement renderer)
    {
        var field = RequiredString(renderer, "field", "'dynamicLayers' classBreaks renderers must name 'field'.");
        if (!renderer.TryGetProperty("minValue", out var min) || min.ValueKind != JsonValueKind.Number)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' classBreaks renderers must carry a numeric 'minValue'.");
        }

        if (!renderer.TryGetProperty("classBreakInfos", out var breaks)
            || breaks.ValueKind != JsonValueKind.Array
            || breaks.GetArrayLength() == 0)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' classBreaks renderers must carry a non-empty 'classBreakInfos'.");
        }

        var ordered = breaks.EnumerateArray()
            .Select(entry => ClassBreak(entry))
            .OrderBy(entry => entry.Max)
            .ToArray();
        var fragments = new JsonArray();
        var lower = min.GetRawText();
        foreach (var entry in ordered)
        {
            var filter = new JsonArray
            {
                "all",
                new JsonArray { ">=", field, JsonNode.Parse(lower) },
                new JsonArray { "<", field, JsonNode.Parse(entry.MaxText) },
            };
            fragments.Add(Fragment(entry.Symbol, filter));
            lower = entry.MaxText;
        }

        return fragments.ToJsonString();
    }

    private static (double Max, string MaxText, JsonElement Symbol) ClassBreak(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("classMaxValue", out var max)
            || max.ValueKind != JsonValueKind.Number
            || !entry.TryGetProperty("symbol", out var symbol)
            || symbol.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'dynamicLayers' 'classBreakInfos' entries must carry a numeric 'classMaxValue' and a 'symbol' object.");
        }

        return (max.GetDouble(), max.GetRawText(), symbol);
    }

    private static JsonObject Fragment(JsonElement symbol, JsonArray? filter)
    {
        var (kind, paint) = SymbolPaint(symbol);
        var fragment = new JsonObject { ["type"] = kind, ["paint"] = paint };
        if (filter is not null)
        {
            fragment["filter"] = filter;
        }

        return fragment;
    }

    private static JsonElement RequireSymbol(JsonElement renderer) =>
        renderer.TryGetProperty("symbol", out var symbol) && symbol.ValueKind == JsonValueKind.Object
            ? symbol
            : throw EsriInteropException.Invalid("'dynamicLayers' simple renderers must carry a 'symbol' object.");

    private static (string Kind, JsonObject Paint) SymbolPaint(JsonElement symbol)
    {
        var type = symbol.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        return type switch
        {
            "esriSMS" => ("circle", MarkerPaint(symbol)),
            "esriSLS" => ("line", LinePaint(symbol)),
            "esriSFS" => ("fill", FillPaint(symbol)),
            _ => throw EsriInteropException.Invalid(
                $"'dynamicLayers' symbol type '{type ?? "missing"}' is not supported: only simple markers, lines and fills (esriSMS, esriSLS, esriSFS) can be restyled per request."),
        };
    }

    private static JsonObject MarkerPaint(JsonElement symbol)
    {
        RequireStyle(symbol, "style", "esriSMSCircle");
        RequireZeroOffsets(symbol);
        var (hex, opacity) = Rgba(symbol, "color");
        var paint = new JsonObject
        {
            ["circle-color"] = hex,
            ["circle-radius"] = Number(symbol, "size", 10.0) / 2,
            ["circle-opacity"] = opacity,
        };
        if (symbol.TryGetProperty("outline", out var outline) && outline.ValueKind == JsonValueKind.Object)
        {
            var (stroke, _) = Rgba(outline, "color");
            paint["circle-stroke-color"] = stroke;
            paint["circle-stroke-width"] = Number(outline, "width", 1.0);
        }

        return paint;
    }

    private static JsonObject LinePaint(JsonElement symbol)
    {
        RequireStyle(symbol, "style", "esriSLSSolid");
        var (hex, opacity) = Rgba(symbol, "color");
        return new JsonObject
        {
            ["line-color"] = hex,
            ["line-width"] = Number(symbol, "width", 1.0),
            ["line-opacity"] = opacity,
        };
    }

    private static JsonObject FillPaint(JsonElement symbol)
    {
        RequireStyle(symbol, "style", "esriSFSSolid");
        var (hex, opacity) = Rgba(symbol, "color");
        var paint = new JsonObject
        {
            ["fill-color"] = hex,
            ["fill-opacity"] = opacity,
        };
        if (symbol.TryGetProperty("outline", out var outline) && outline.ValueKind == JsonValueKind.Object)
        {
            var (stroke, _) = Rgba(outline, "color");
            paint["fill-outline-color"] = stroke;
            paint["fill-outline-width"] = Number(outline, "width", 1.0);
        }

        return paint;
    }

    private static void RequireStyle(JsonElement symbol, string name, string supported)
    {
        var style = symbol.TryGetProperty(name, out var styleElement) && styleElement.ValueKind == JsonValueKind.String
            ? styleElement.GetString()
            : null;
        if (!string.Equals(style, supported, StringComparison.Ordinal))
        {
            throw EsriInteropException.Invalid(
                $"'dynamicLayers' symbol style '{style ?? "missing"}' is not supported: only '{supported}' restyles per request.");
        }
    }

    private static void RequireZeroOffsets(JsonElement symbol)
    {
        foreach (var name in new[] { "xoffset", "yoffset" })
        {
            if (symbol.TryGetProperty(name, out var offset)
                && offset.ValueKind == JsonValueKind.Number
                && offset.GetDouble() != 0)
            {
                throw EsriInteropException.Invalid(
                    $"'dynamicLayers' symbol '{name}' is not supported: per-request symbols draw unshifted.");
            }
        }
    }

    private static (string Hex, double Opacity) Rgba(JsonElement holder, string name)
    {
        if (!holder.TryGetProperty(name, out var color) || color.ValueKind != JsonValueKind.Array)
        {
            return ("#000000", 1.0);
        }

        var channels = color.EnumerateArray()
            .Where(channel => channel.ValueKind == JsonValueKind.Number)
            .Select(channel => channel.GetDouble())
            .ToArray();
        if (channels.Length is not (3 or 4) || channels.Any(channel => channel is < 0 or > 255))
        {
            throw EsriInteropException.Invalid("'dynamicLayers' symbol colors must be [r,g,b] or [r,g,b,a] with channels 0-255.");
        }

        var hex = FormattableString.Invariant($"#{(int)channels[0]:x2}{(int)channels[1]:x2}{(int)channels[2]:x2}");
        var opacity = channels.Length == 4 ? Math.Round(channels[3] / 255, 3) : 1.0;
        return (hex, opacity);
    }

    private static double Number(JsonElement holder, string name, double fallback)
    {
        if (!holder.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return property.ValueKind == JsonValueKind.Number && property.GetDouble() >= 0
            ? property.GetDouble()
            : throw EsriInteropException.Invalid($"'dynamicLayers' symbol '{name}' must be a non-negative number.");
    }

    private static string RequiredString(JsonElement holder, string name, string message) =>
        holder.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!
                : throw EsriInteropException.Invalid(message);

    private static int Integer(JsonElement holder, string name, string message)
    {
        if (holder.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw EsriInteropException.Invalid(message);
    }
}

/// <summary>One parsed dynamic layer: its render identity, the rebound published layer and the optional style override.</summary>
internal sealed record DynamicLayerOverride(int Id, int MapLayerId, string? Name, string? StyleOverride);
