using System.Globalization;
using System.Text.Json;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Projects a publication layer's persisted MapLibre style fragment
/// (ADR-0047) onto an Esri <c>drawingInfo</c> simple renderer (spec §12).
/// The mapping is deliberately narrow: a <c>fill</c> fragment becomes an
/// <c>esriSFS</c>, a <c>line</c> an <c>esriSLS</c> and a <c>circle</c> an
/// <c>esriSMS</c>; class-break, unique-value and label renderers are not
/// produced because the renderer has no such model (ADR-0044/ADR-0048). A
/// layer with no persisted style yields no <c>drawingInfo</c>.
/// </summary>
internal static class MapStyleProjection
{
    /// <summary>The drawing info for a persisted style fragment, or null when there is none.</summary>
    public static EsriDrawingInfo? Project(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(style);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var fragments = document.RootElement.EnumerateArray().ToArray();
            return Symbol(Fragment(fragments, "fill"))
                ?? Symbol(Fragment(fragments, "line"))
                ?? Symbol(Fragment(fragments, "circle"));
        }
    }

    private static JsonElement? Fragment(JsonElement[] fragments, string type)
    {
        foreach (var fragment in fragments)
        {
            if (fragment.ValueKind == JsonValueKind.Object
                && fragment.TryGetProperty("type", out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), type, StringComparison.Ordinal))
            {
                return fragment;
            }
        }

        return null;
    }

    private static EsriDrawingInfo? Symbol(JsonElement? fragment)
    {
        if (fragment is not { } element)
        {
            return null;
        }

        var paint = element.TryGetProperty("paint", out var paintElement) && paintElement.ValueKind == JsonValueKind.Object
            ? paintElement
            : default;
        return element.GetProperty("type").GetString() switch
        {
            "fill" => Fill(paint),
            "line" => Line(paint),
            "circle" => Circle(paint),
            _ => null,
        };
    }

    private static EsriDrawingInfo Fill(JsonElement paint)
    {
        var opacity = Number(paint, "fill-opacity", 1.0);
        var fill = Color(paint, "fill-color", "#000000", opacity);
        var outlineWidth = Number(paint, "fill-outline-width", 0);
        var outlineColor = Color(paint, "fill-outline-color", null, opacity);
        var outline = outlineWidth > 0 && outlineColor is { } stroke
            ? new EsriSymbolOutline("esriSLS", "esriSLSSolid", stroke, outlineWidth)
            : null;
        return new EsriDrawingInfo(new EsriRenderer("simple", new EsriSymbol("esriSFS", "esriSFSSolid", fill, Outline: outline)));
    }

    private static EsriDrawingInfo Line(JsonElement paint)
    {
        var opacity = Number(paint, "line-opacity", 1.0);
        var color = Color(paint, "line-color", "#000000", opacity);
        var width = Number(paint, "line-width", 1.0);
        return new EsriDrawingInfo(new EsriRenderer("simple", new EsriSymbol("esriSLS", "esriSLSSolid", color, Width: width)));
    }

    private static EsriDrawingInfo Circle(JsonElement paint)
    {
        var opacity = Number(paint, "circle-opacity", 1.0);
        var color = Color(paint, "circle-color", "#000000", opacity);
        var radius = Number(paint, "circle-radius", 5.0);
        var strokeWidth = Number(paint, "circle-stroke-width", 0);
        var strokeColor = Color(paint, "circle-stroke-color", null, opacity);
        var outline = strokeWidth > 0 && strokeColor is { } stroke
            ? new EsriSymbolOutline("esriSLS", "esriSLSSolid", stroke, strokeWidth)
            : null;
        return new EsriDrawingInfo(new EsriRenderer("simple", new EsriSymbol("esriSMS", "esriSMSCircle", color, Size: radius * 2, Outline: outline)));
    }

    private static int[] Color(JsonElement paint, string name, string? fallback, double opacity)
    {
        var css = paint.ValueKind == JsonValueKind.Object
            && paint.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : fallback;
        return EsriColor.ToRgba(css, opacity);
    }

    private static double Number(JsonElement paint, string name, double fallback) =>
        paint.ValueKind == JsonValueKind.Object
        && paint.TryGetProperty(name, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
            ? value
            : fallback;
}

/// <summary>
/// The CSS-ish colour subset the persisted style uses (hex, <c>rgb()</c>/
/// <c>rgba()</c> and a few names) projected to an Esri RGBA quad. MapLibre
/// accepts more syntaxes; the composer emits hex, and an unparsable value
/// falls back to transparent rather than failing the whole layer.
/// </summary>
internal static class EsriColor
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["white"] = "#ffffff",
        ["red"] = "#ff0000",
        ["green"] = "#008000",
        ["blue"] = "#0000ff",
        ["yellow"] = "#ffff00",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["transparent"] = "#00000000",
    };

    /// <summary>Parses a colour and applies an extra opacity (0–1) to its alpha.</summary>
    public static int[] ToRgba(string? css, double opacity)
    {
        var rgba = Parse(css) ?? [0, 0, 0, 0];
        rgba[3] = (int)Math.Round(Math.Clamp(rgba[3] / 255.0 * opacity, 0, 1) * 255);
        return rgba;
    }

    private static int[]? Parse(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return null;
        }

        var text = css.Trim();
        if (Names.TryGetValue(text, out var named))
        {
            text = named;
        }

        if (text.StartsWith('#'))
        {
            return ParseHex(text[1..]);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseFunctional(text);
        }

        return null;
    }

    private static int[]? ParseHex(string hex)
    {
        return hex.Length switch
        {
            3 => [Channel(hex[0]), Channel(hex[1]), Channel(hex[2]), 255],
            4 => [Channel(hex[0]), Channel(hex[1]), Channel(hex[2]), Channel(hex[3])],
            6 => [Byte(hex, 0), Byte(hex, 2), Byte(hex, 4), 255],
            8 => [Byte(hex, 0), Byte(hex, 2), Byte(hex, 4), Byte(hex, 6)],
            _ => null,
        };
    }

    private static int[]? ParseFunctional(string text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var parts = text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4))
        {
            return null;
        }

        var channels = new int[4];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            channels[i] = (int)Math.Clamp(Math.Round(value), 0, 255);
        }

        channels[3] = 255;
        if (parts.Length == 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            channels[3] = (int)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        }

        return channels;
    }

    private static int Byte(string hex, int index) =>
        int.TryParse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int Channel(char value) => Byte(new string(value, 2), 0);
}
