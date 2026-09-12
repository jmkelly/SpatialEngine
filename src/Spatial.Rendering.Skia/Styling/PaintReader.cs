using System.Globalization;
using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads the paint recipe for one style layer's type from a MapLibre
/// <c>paint</c> object. Unsupported properties are rejected with a typed
/// <c>invalid.arguments</c> naming the property — the documented subset is
/// never silently flattened.
/// </summary>
internal static class PaintReader
{
    public static PaintRecipe Read(DrawKind kind, JsonElement paint) => kind switch
    {
        DrawKind.Background => ReadBackground(new PaintBag(paint)),
        DrawKind.Fill => ReadFill(new PaintBag(paint)),
        DrawKind.Line => ReadLine(new PaintBag(paint)),
        DrawKind.Circle => ReadCircle(new PaintBag(paint)),
        _ => throw SpatialException.BadArguments($"Unsupported layer kind '{kind}'."),
    };

    private static BackgroundPaint ReadBackground(PaintBag bag)
    {
        var color = bag.Color("background-color", StyleColor.Transparent);
        var opacity = bag.Number("background-opacity", 1.0);
        bag.RejectUnsupported();
        return new BackgroundPaint(color.ScaleAlpha(opacity));
    }

    private static FillPaint ReadFill(PaintBag bag)
    {
        var opacity = bag.Number("fill-opacity", 1.0);
        var fill = bag.Color("fill-color", new StyleColor(0, 0, 0)).ScaleAlpha(opacity);
        var outline = bag.Color("fill-outline-color", StyleColor.Transparent).ScaleAlpha(opacity);
        var width = bag.Number("fill-outline-width", 0.0);
        bag.RejectUnsupported();
        AssertNonNegative(width, "fill-outline-width");
        return new FillPaint(fill, outline, width);
    }

    private static LinePaint ReadLine(PaintBag bag)
    {
        var opacity = bag.Number("line-opacity", 1.0);
        var stroke = bag.Color("line-color", new StyleColor(0, 0, 0)).ScaleAlpha(opacity);
        var width = bag.Number("line-width", 1.0);
        var dash = bag.NumberList("line-dasharray");
        var cap = bag.LineCap("line-cap", LineCapStyle.Butt);
        var join = bag.LineJoin("line-join", LineJoinStyle.Miter);
        bag.RejectUnsupported();
        AssertNonNegative(width, "line-width");
        return new LinePaint(stroke, width, dash, cap, join);
    }

    private static CirclePaint ReadCircle(PaintBag bag)
    {
        var opacity = bag.Number("circle-opacity", 1.0);
        var fill = bag.Color("circle-color", new StyleColor(0, 0, 0)).ScaleAlpha(opacity);
        var radius = bag.Number("circle-radius", 5.0);
        var stroke = bag.Color("circle-stroke-color", StyleColor.Transparent).ScaleAlpha(opacity);
        var strokeWidth = bag.Number("circle-stroke-width", 0.0);
        bag.RejectUnsupported();
        AssertNonNegative(radius, "circle-radius");
        AssertNonNegative(strokeWidth, "circle-stroke-width");
        return new CirclePaint(fill, radius, stroke, strokeWidth);
    }

    private static void AssertNonNegative(double value, string property)
    {
        if (value < 0)
        {
            throw SpatialException.BadArguments($"'{property}' must be non-negative, got {value.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>Wraps the paint object and tracks which properties were consumed.</summary>
    private sealed class PaintBag
    {
        private readonly JsonElement _paint;
        private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

        public PaintBag(JsonElement paint) => _paint = paint;

        public StyleColor Color(string name, StyleColor fallback)
        {
            if (!TryGet(name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind != JsonValueKind.String || !StyleColorParser.TryParse(value.GetString(), out var color))
            {
                throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a CSS colour.");
            }

            return color;
        }

        public double Number(string name, double fallback)
        {
            if (!TryGet(name, out var value))
            {
                return fallback;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            {
                throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a number.");
            }

            return number;
        }

        public List<double> NumberList(string name)
        {
            var result = new List<double>();
            if (!TryGet(name, out var value))
            {
                return result;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw SpatialException.BadArguments($"Unsupported value for '{name}': expected an array of numbers.");
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number))
                {
                    throw SpatialException.BadArguments($"Unsupported value for '{name}': expected an array of numbers.");
                }

                result.Add(number);
            }

            return result;
        }

        public LineCapStyle LineCap(string name, LineCapStyle fallback)
        {
            if (!TryGet(name, out var value))
            {
                return fallback;
            }

            var text = ReadString(value, name);
            return text switch
            {
                "butt" => LineCapStyle.Butt,
                "round" => LineCapStyle.Round,
                "square" => LineCapStyle.Square,
                _ => throw SpatialException.BadArguments($"Unsupported value for '{name}': '{text}'."),
            };
        }

        public LineJoinStyle LineJoin(string name, LineJoinStyle fallback)
        {
            if (!TryGet(name, out var value))
            {
                return fallback;
            }

            var text = ReadString(value, name);
            return text switch
            {
                "miter" => LineJoinStyle.Miter,
                "round" => LineJoinStyle.Round,
                "bevel" => LineJoinStyle.Bevel,
                _ => throw SpatialException.BadArguments($"Unsupported value for '{name}': '{text}'."),
            };
        }

        public void RejectUnsupported()
        {
            if (_paint.ValueKind == JsonValueKind.Undefined)
            {
                return;
            }

            if (_paint.ValueKind != JsonValueKind.Object)
            {
                throw SpatialException.BadArguments("'paint' must be a JSON object.");
            }

            foreach (var property in _paint.EnumerateObject())
            {
                if (!_consumed.Contains(property.Name))
                {
                    throw SpatialException.BadArguments($"Unsupported paint property '{property.Name}'.");
                }
            }
        }

        private bool TryGet(string name, out JsonElement value)
        {
            if (_paint.ValueKind == JsonValueKind.Object && _paint.TryGetProperty(name, out value))
            {
                _consumed.Add(name);
                return true;
            }

            value = default;
            return false;
        }

        private static string ReadString(JsonElement value, string name)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a string.");
            }

            return value.GetString() ?? string.Empty;
        }
    }
}
