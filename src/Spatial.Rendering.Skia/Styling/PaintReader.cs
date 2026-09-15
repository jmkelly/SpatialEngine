using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads the paint recipe for one style layer's type from a MapLibre
/// <c>paint</c> object (and, for <c>symbol</c>, its <c>layout</c> object).
/// Unsupported properties are rejected with a typed <c>invalid.arguments</c>
/// naming the property — the documented subset is never silently flattened.
/// </summary>
internal static class PaintReader
{
    public static PaintRecipe Read(DrawKind kind, JsonElement paint) => Read(kind, paint, default);

    public static PaintRecipe Read(DrawKind kind, JsonElement paint, JsonElement layout) => kind switch
    {
        DrawKind.Background => ReadBackground(new StylePropertyBag(paint, "paint")),
        DrawKind.Fill => ReadFill(new StylePropertyBag(paint, "paint")),
        DrawKind.Line => ReadLine(new StylePropertyBag(paint, "paint")),
        DrawKind.Circle => ReadCircle(new StylePropertyBag(paint, "paint")),
        DrawKind.Symbol => SymbolReader.Read(layout, paint),
        _ => throw SpatialException.BadArguments($"Unsupported layer kind '{kind}'."),
    };

    private static BackgroundPaint ReadBackground(StylePropertyBag bag)
    {
        var color = bag.Color("background-color", StyleColor.Transparent);
        var opacity = bag.Number("background-opacity", 1.0);
        bag.RejectUnsupported();
        return new BackgroundPaint(color.ScaleAlpha(opacity));
    }

    private static FillPaint ReadFill(StylePropertyBag bag)
    {
        var opacity = bag.Number("fill-opacity", 1.0);
        var fill = bag.Color("fill-color", new StyleColor(0, 0, 0)).ScaleAlpha(opacity);
        var outline = bag.Color("fill-outline-color", StyleColor.Transparent).ScaleAlpha(opacity);
        var width = bag.Number("fill-outline-width", 0.0);
        bag.RejectUnsupported();
        AssertNonNegative(width, "fill-outline-width");
        return new FillPaint(fill, outline, width);
    }

    private static LinePaint ReadLine(StylePropertyBag bag)
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

    private static CirclePaint ReadCircle(StylePropertyBag bag)
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

    internal static void AssertNonNegative(double value, string property)
    {
        if (value < 0)
        {
            throw SpatialException.BadArguments(
                $"'{property}' must be non-negative, got {value.ToString(CultureInfo.InvariantCulture)}.");
        }
    }
}
