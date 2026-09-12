namespace RenderSpike;

using SkiaSharp;

/// <summary>
/// The compiled style model: a paint recipe per source layer. A CSS parser
/// (here a deliberately tiny one; production should use ExCSS or the
/// MapLibre style spec) lowers text to this form, and a style compiler turns
/// each recipe into Skia paints once per render, not per feature.
/// </summary>
public abstract record VectorStyle;

public sealed record FillStyle(SKColor Fill, float Opacity, SKColor Outline, float OutlineWidth) : VectorStyle;

public sealed record LineStyle(SKColor Stroke, float Width, float Opacity, float[] Dash) : VectorStyle;

public sealed record CircleStyle(SKColor Fill, float Radius, float Opacity, SKColor Stroke, float StrokeWidth) : VectorStyle;

/// <summary>A stylesheet mapping a source layer id to its paint recipe.</summary>
public sealed class StyleSheet
{
    private readonly Dictionary<string, VectorStyle> _byLayer;

    private StyleSheet(Dictionary<string, VectorStyle> byLayer) => _byLayer = byLayer;

    public bool TryGet(string layer, out VectorStyle style) => _byLayer.TryGetValue(layer, out style!);

    public IReadOnlyDictionary<string, VectorStyle> Layers => _byLayer;

    /// <summary>
    /// Parses a spike-grade subset of CSS: <c>#layer { prop: value; … }</c>.
    /// Supported props: fill, fill-opacity, fill-outline, fill-outline-width,
    /// stroke, stroke-width, stroke-opacity, stroke-dash, circle,
    /// circle-radius, circle-opacity, circle-stroke, circle-stroke-width.
    /// </summary>
    public static StyleSheet Parse(string css)
    {
        var byLayer = new Dictionary<string, VectorStyle>(StringComparer.Ordinal);
        foreach (var block in SplitBlocks(css))
        {
            var (selector, body) = block;
            var layer = selector.Trim().TrimStart('#', '.');
            if (layer.Length == 0)
            {
                continue;
            }

            byLayer[layer] = Build(ParseDeclarations(body));
        }

        return new StyleSheet(byLayer);
    }

    private static IEnumerable<(string Selector, string Body)> SplitBlocks(string css)
    {
        var text = StripComments(css);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            if (open < 0)
            {
                yield break;
            }

            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                yield break;
            }

            yield return (text[index..open], text[(open + 1)..close]);
            index = close + 1;
        }
    }

    private static Dictionary<string, string> ParseDeclarations(string body)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            declarations[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
        }

        return declarations;
    }

    private static VectorStyle Build(Dictionary<string, string> props)
    {
        if (props.ContainsKey("circle") || props.ContainsKey("circle-radius"))
        {
            return new CircleStyle(
                Color(props, "circle", SKColors.White),
                Number(props, "circle-radius", 4f),
                Number(props, "circle-opacity", 1f),
                Color(props, "circle-stroke", SKColors.Transparent),
                Number(props, "circle-stroke-width", 0f));
        }

        if (props.ContainsKey("stroke") || props.ContainsKey("stroke-width"))
        {
            return new LineStyle(
                Color(props, "stroke", SKColors.White),
                Number(props, "stroke-width", 1f),
                Number(props, "stroke-opacity", 1f),
                Dash(props.GetValueOrDefault("stroke-dash")));
        }

        return new FillStyle(
            Color(props, "fill", SKColors.Gray),
            Number(props, "fill-opacity", 1f),
            Color(props, "fill-outline", SKColors.Transparent),
            Number(props, "fill-outline-width", 0f));
    }

    private static float Number(Dictionary<string, string> props, string key, float fallback) =>
        props.TryGetValue(key, out var raw) && float.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static SKColor Color(Dictionary<string, string> props, string key, SKColor fallback) =>
        props.TryGetValue(key, out var raw) && TryParseColor(raw, out var color) ? color : fallback;

    private static float[] Dash(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => float.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0f)
                .Where(value => value > 0)
                .ToArray();

    private static bool TryParseColor(string raw, out SKColor color)
    {
        var text = raw.Trim();
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length is 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length is 6 or 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                color = hex.Length == 6
                    ? new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value)
                    : new SKColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }
        }

        color = text.ToLowerInvariant() switch
        {
            "white" => SKColors.White,
            "black" => SKColors.Black,
            "transparent" => SKColors.Transparent,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            _ => SKColors.Transparent,
        };
        return text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || color != SKColors.Transparent;
    }

    private static string StripComments(string css)
    {
        var result = new System.Text.StringBuilder(css.Length);
        for (var i = 0; i < css.Length; i++)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 1;
                continue;
            }

            result.Append(css[i]);
        }

        return result.ToString();
    }
}
