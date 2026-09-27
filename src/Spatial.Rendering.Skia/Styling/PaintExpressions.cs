using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// The data-driven half of a compiled paint recipe: one expression per
/// property that can vary per feature, all <c>null</c> when the style is
/// constant. A recipe with no expressions resolves to itself, so a constant
/// style compiles to exactly the recipe it compiled to before the expression
/// dialect existed — the committed golden renders are byte-identical.
/// </summary>
internal static class PaintExpressions
{
    /// <summary>
    /// Resolves a colour against its resolved opacity. The recipe's constant
    /// colour is stored untinted whenever the recipe is data-driven, so the
    /// opacity is applied here exactly once, whether it came from a constant or
    /// from an expression.
    /// </summary>
    public static StyleColor Tinted(
        ExpressionScope scope, StyleExpression? color, double alpha, string property, StyleColor fallback)
    {
        if (color is null)
        {
            return fallback.ScaleAlpha(alpha);
        }

        var value = scope.Evaluate(color);
        if (value.IsMissing)
        {
            return fallback.ScaleAlpha(alpha);
        }

        return value.Type == ExpressionType.Color
            ? value.Color.ScaleAlpha(alpha)
            : throw SpatialException.BadArguments(
                $"'{property}' expects a colour, but the expression {color} yielded {value} for this feature.");
    }

    /// <summary>
    /// Evaluates a size property. A size whose value is only known per feature
    /// is checked here, not at compile time: a negative radius is a typed
    /// <c>invalid.arguments</c> naming the property, exactly as a negative
    /// literal radius is rejected by the reader.
    /// </summary>
    public static double Size(ExpressionScope scope, StyleExpression? expression, string property, double fallback)
    {
        var value = Number(scope, expression, property, fallback);
        if (expression is not null && value < 0)
        {
            throw SpatialException.BadArguments(
                $"'{property}' must be non-negative, but the expression {expression} yielded {value} for this feature.");
        }

        return value;
    }

    /// <summary>Evaluates a number property, with the same missing/type discipline as <see cref="Color"/>.</summary>
    public static double Number(ExpressionScope scope, StyleExpression? expression, string property, double fallback)
    {
        if (expression is null)
        {
            return fallback;
        }

        var value = scope.Evaluate(expression);
        if (value.IsMissing)
        {
            return fallback;
        }

        return value.Type == ExpressionType.Number
            ? value.AsNumber()
            : throw SpatialException.BadArguments(
                $"'{property}' expects a number, but the expression {expression} yielded {value} for this feature.");
    }
}

// Every *Expressions record carries the authored `ConstantOpacity`: a recipe
// with no expressions never gets one, because its constants already carry the
// alpha, while a data-driven recipe stores its colours untinted and applies the
// alpha once per feature during resolution.

/// <summary>The per-feature paint of a <c>fill</c> layer.</summary>
internal sealed record FillExpressions(
    StyleExpression? Color = null,
    StyleExpression? Opacity = null,
    StyleExpression? Outline = null,
    StyleExpression? OutlineWidth = null,
    double ConstantOpacity = 1.0);

/// <summary>The per-feature paint of a <c>line</c> layer.</summary>
internal sealed record LineExpressions(
    StyleExpression? Color = null,
    StyleExpression? Opacity = null,
    StyleExpression? Width = null,
    double ConstantOpacity = 1.0);

/// <summary>The per-feature paint of a <c>circle</c> layer.</summary>
internal sealed record CircleExpressions(
    StyleExpression? Color = null,
    StyleExpression? Opacity = null,
    StyleExpression? Radius = null,
    StyleExpression? StrokeColor = null,
    StyleExpression? StrokeWidth = null,
    double ConstantOpacity = 1.0);

/// <summary>The paint of a <c>background</c> layer (zoom-driven: it has no features).</summary>
internal sealed record BackgroundExpressions(
    StyleExpression? Color = null,
    StyleExpression? Opacity = null,
    double ConstantOpacity = 1.0);

/// <summary>The per-feature paint of a <c>symbol</c> layer's text (layout is static).</summary>
internal sealed record SymbolExpressions(
    StyleExpression? Color = null,
    StyleExpression? Opacity = null,
    StyleExpression? HaloColor = null,
    StyleExpression? HaloWidth = null,
    double ConstantOpacity = 1.0);
