using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads the paint recipe for one style layer's type from a MapLibre
/// <c>paint</c> object (and, for <c>symbol</c>, its <c>layout</c> object).
/// Every documented property compiles either to a constant or to a
/// data-driven expression; unsupported properties are rejected with a typed
/// <c>invalid.arguments</c> naming the property — the documented subset is
/// never silently flattened.
/// </summary>
internal static class PaintReader
{
    public static PaintRecipe Read(DrawKind kind, JsonElement paint) => Read(kind, paint, default, null);

    public static PaintRecipe Read(DrawKind kind, JsonElement paint, JsonElement layout, ExpressionInterner? interner = null) => kind switch
    {
        DrawKind.Background => ReadBackground(new StylePropertyBag(paint, "paint", interner)),
        DrawKind.Fill => ReadFill(new StylePropertyBag(paint, "paint", interner)),
        DrawKind.Line => ReadLine(new StylePropertyBag(paint, "paint", interner)),
        DrawKind.Circle => ReadCircle(new StylePropertyBag(paint, "paint", interner)),
        DrawKind.Symbol => SymbolReader.Read(layout, paint, interner),
        _ => throw SpatialException.BadArguments($"Unsupported layer kind '{kind}'."),
    };

    private static BackgroundPaint ReadBackground(StylePropertyBag bag)
    {
        var (color, colorExpression) = bag.Color("background-color", StyleColor.Transparent);
        var (opacity, opacityExpression) = bag.Number("background-opacity", 1.0);
        bag.RejectUnsupported();
        var dataDriven = colorExpression is not null || opacityExpression is not null;
        return new BackgroundPaint(
            color.ScaleAlpha(CompileAlpha(opacity, dataDriven)),
            dataDriven ? new BackgroundExpressions(colorExpression, opacityExpression, opacity) : null);
    }

    private static FillPaint ReadFill(StylePropertyBag bag)
    {
        var (opacity, opacityExpression) = bag.Number("fill-opacity", 1.0);
        var (fill, fillExpression) = bag.Color("fill-color", new StyleColor(0, 0, 0));
        var (outline, outlineExpression) = bag.Color("fill-outline-color", StyleColor.Transparent);
        var (width, widthExpression) = bag.Number("fill-outline-width", 0.0);
        bag.RejectUnsupported();
        AssertNonNegative(width, "fill-outline-width");
        var dataDriven = fillExpression is not null || opacityExpression is not null
            || outlineExpression is not null || widthExpression is not null;
        var alpha = CompileAlpha(opacity, dataDriven);
        return new FillPaint(
            fill.ScaleAlpha(alpha),
            outline.ScaleAlpha(alpha),
            width,
            dataDriven
                ? new FillExpressions(fillExpression, opacityExpression, outlineExpression, widthExpression, opacity)
                : null);
    }

    private static LinePaint ReadLine(StylePropertyBag bag)
    {
        var (opacity, opacityExpression) = bag.Number("line-opacity", 1.0);
        var (stroke, strokeExpression) = bag.Color("line-color", new StyleColor(0, 0, 0));
        var (width, widthExpression) = bag.Number("line-width", 1.0);
        var dash = bag.NumberList("line-dasharray");
        var cap = bag.LineCap("line-cap", LineCapStyle.Butt);
        var join = bag.LineJoin("line-join", LineJoinStyle.Miter);
        bag.RejectUnsupported();
        AssertNonNegative(width, "line-width");
        var dataDriven = strokeExpression is not null || opacityExpression is not null || widthExpression is not null;
        return new LinePaint(
            stroke.ScaleAlpha(CompileAlpha(opacity, dataDriven)),
            width,
            dash,
            cap,
            join,
            dataDriven ? new LineExpressions(strokeExpression, opacityExpression, widthExpression, opacity) : null);
    }

    private static CirclePaint ReadCircle(StylePropertyBag bag)
    {
        var (opacity, opacityExpression) = bag.Number("circle-opacity", 1.0);
        var (fill, fillExpression) = bag.Color("circle-color", new StyleColor(0, 0, 0));
        var (radius, radiusExpression) = bag.Number("circle-radius", 5.0);
        var (stroke, strokeExpression) = bag.Color("circle-stroke-color", StyleColor.Transparent);
        var (strokeWidth, strokeWidthExpression) = bag.Number("circle-stroke-width", 0.0);
        bag.RejectUnsupported();
        AssertNonNegative(radius, "circle-radius");
        AssertNonNegative(strokeWidth, "circle-stroke-width");
        var dataDriven = fillExpression is not null || opacityExpression is not null || radiusExpression is not null
            || strokeExpression is not null || strokeWidthExpression is not null;
        var alpha = CompileAlpha(opacity, dataDriven);
        return new CirclePaint(
            fill.ScaleAlpha(alpha),
            radius,
            stroke.ScaleAlpha(alpha),
            strokeWidth,
            dataDriven
                ? new CircleExpressions(
                    fillExpression, opacityExpression, radiusExpression, strokeExpression, strokeWidthExpression, opacity)
                : null);
    }

    /// <summary>
    /// The alpha a recipe's constant colours carry at compile time: the
    /// authored opacity for a constant recipe, and 1 for a data-driven one,
    /// whose colours are stored untinted so the per-feature resolution applies
    /// the alpha exactly once.
    /// </summary>
    private static double CompileAlpha(double opacity, bool dataDriven) => dataDriven ? 1.0 : opacity;

    internal static void AssertNonNegative(double value, string property)
    {
        if (value < 0)
        {
            throw SpatialException.BadArguments(
                $"'{property}' must be non-negative, got {value.ToString(CultureInfo.InvariantCulture)}.");
        }
    }
}
