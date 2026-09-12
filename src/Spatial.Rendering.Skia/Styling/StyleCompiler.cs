using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Compiles a MapLibre-style document into the internal <see cref="CompiledStyle"/>
/// draw plan. The documented subset is <c>background</c>, <c>fill</c>,
/// <c>line</c> and <c>circle</c> layers with a flat paint recipe and a
/// MapLibre <c>filter</c> expression; unsupported types and paint properties
/// fail with a typed <c>invalid.arguments</c> instead of being flattened.
/// Compiled plans are cached by the style document hash.
/// </summary>
internal sealed class StyleCompiler
{
    private const double DefaultMaxZoom = 24;
    private readonly ConcurrentDictionary<string, CompiledStyle> _cache = new(StringComparer.Ordinal);

    public CompiledStyle Compile(string style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            throw SpatialException.BadArguments("A style document is required.");
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
        return _cache.GetOrAdd(key, _ => Parse(style));
    }

    private static CompiledStyle Parse(string style)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(style);
        }
        catch (JsonException exception)
        {
            throw SpatialException.BadArguments($"The style document is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw SpatialException.BadArguments("The style document must be a JSON object.");
            }

            if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
            {
                throw SpatialException.BadArguments("The style document must declare a 'layers' array.");
            }

            var compiled = new List<DrawLayer>(layers.GetArrayLength());
            foreach (var layer in layers.EnumerateArray())
            {
                compiled.Add(ReadLayer(layer));
            }

            if (compiled.Count == 0)
            {
                throw SpatialException.BadArguments("The style document declares no layers.");
            }

            return new CompiledStyle(compiled);
        }
    }

    private static DrawLayer ReadLayer(JsonElement layer)
    {
        if (layer.ValueKind != JsonValueKind.Object)
        {
            throw SpatialException.BadArguments("Every style layer must be a JSON object.");
        }

        var id = RequiredString(layer, "id");
        var kind = ReadKind(RequiredString(layer, "type"));
        var dataset = kind == DrawKind.Background ? null : RequiredString(layer, "source-layer");
        var minZoom = OptionalNumber(layer, "minzoom", 0);
        var maxZoom = OptionalNumber(layer, "maxzoom", DefaultMaxZoom);
        if (minZoom >= maxZoom)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Layer '{id}' has minzoom {minZoom} >= maxzoom {maxZoom}."));
        }

        var visible = ReadVisibility(layer);
        var filter = layer.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind != JsonValueKind.Null
            ? FilterReader.Read(filterElement)
            : StyleFilter.Always;
        var paint = layer.TryGetProperty("paint", out var paintElement)
            ? PaintReader.Read(kind, paintElement)
            : PaintReader.Read(kind, default);
        return new DrawLayer(id, dataset, kind, minZoom, maxZoom, visible, filter, paint);
    }

    private static DrawKind ReadKind(string type) => type switch
    {
        "background" => DrawKind.Background,
        "fill" => DrawKind.Fill,
        "line" => DrawKind.Line,
        "circle" => DrawKind.Circle,
        _ => throw SpatialException.BadArguments($"Unsupported layer type '{type}'."),
    };

    private static bool ReadVisibility(JsonElement layer)
    {
        if (!layer.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!layout.TryGetProperty("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var text = visibility.GetString();
        return text switch
        {
            "visible" => true,
            "none" => false,
            _ => throw SpatialException.BadArguments($"Unsupported layout visibility '{text}'."),
        };
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw SpatialException.BadArguments($"A style layer requires a string '{name}'.");
        }

        return value.GetString()!;
    }

    private static double OptionalNumber(JsonElement element, string name, double fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw SpatialException.BadArguments($"'{name}' must be a number, got {value.ValueKind.ToString()}");
        }

        return number;
    }
}
