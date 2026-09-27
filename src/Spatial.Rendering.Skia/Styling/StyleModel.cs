namespace Spatial.Rendering.Skia.Styling;

/// <summary>The layer kinds the documented MapLibre subset supports.</summary>
internal enum DrawKind
{
    Background,
    Fill,
    Line,
    Circle,
    Symbol,
}

internal enum LineCapStyle
{
    Butt,
    Round,
    Square,
}

internal enum LineJoinStyle
{
    Miter,
    Round,
    Bevel,
}

/// <summary>A colour-plus-geometry draw recipe, compiled once per style layer.</summary>
internal abstract record PaintRecipe
{
    /// <summary>
    /// Resolves the data-driven half against one feature, yielding a constant
    /// recipe the rasterizer can batch. A recipe with no expressions returns
    /// itself, so a constant style pays nothing per feature.
    /// </summary>
    public abstract PaintRecipe Resolve(ExpressionScope scope);

    /// <summary>Whether any of the recipe's properties is data-driven.</summary>
    public abstract bool IsDataDriven();
}

internal sealed record BackgroundPaint(StyleColor Color, BackgroundExpressions? Expressions = null) : PaintRecipe
{
    public override bool IsDataDriven() => Expressions is not null;

    public override PaintRecipe Resolve(ExpressionScope scope) => this with
    {
        Color = Expressions is null ? Color : PaintExpressions.Tinted(
            scope,
            Expressions.Color,
            PaintOpacity.Resolve(scope, Expressions.ConstantOpacity, Expressions.Opacity, "background-opacity"),
            "background-color",
            Color),
        Expressions = null,
    };
}

internal sealed record FillPaint(
    StyleColor Fill,
    StyleColor Outline,
    double OutlineWidth,
    FillExpressions? Expressions = null) : PaintRecipe
{
    public override bool IsDataDriven() => Expressions is not null;

    public override PaintRecipe Resolve(ExpressionScope scope)
    {
        if (Expressions is null)
        {
            return this;
        }

        var alpha = PaintOpacity.Resolve(scope, Expressions.ConstantOpacity, Expressions.Opacity, "fill-opacity");
        return this with
        {
            Fill = PaintExpressions.Tinted(scope, Expressions.Color, alpha, "fill-color", Fill),
            Outline = PaintExpressions.Tinted(scope, Expressions.Outline, alpha, "fill-outline-color", Outline),
            OutlineWidth = PaintExpressions.Size(scope, Expressions.OutlineWidth, "fill-outline-width", OutlineWidth),
            Expressions = null,
        };
    }
}

internal sealed record LinePaint(
    StyleColor Stroke,
    double Width,
    IReadOnlyList<double> Dash,
    LineCapStyle Cap,
    LineJoinStyle Join,
    LineExpressions? Expressions = null) : PaintRecipe
{
    public override bool IsDataDriven() => Expressions is not null;

    public override PaintRecipe Resolve(ExpressionScope scope)
    {
        if (Expressions is null)
        {
            return this;
        }

        var alpha = PaintOpacity.Resolve(scope, Expressions.ConstantOpacity, Expressions.Opacity, "line-opacity");
        return this with
        {
            Stroke = PaintExpressions.Tinted(scope, Expressions.Color, alpha, "line-color", Stroke),
            Width = PaintExpressions.Size(scope, Expressions.Width, "line-width", Width),
            Expressions = null,
        };
    }
}

internal sealed record CirclePaint(
    StyleColor Fill,
    double Radius,
    StyleColor Stroke,
    double StrokeWidth,
    CircleExpressions? Expressions = null) : PaintRecipe
{
    public override bool IsDataDriven() => Expressions is not null;

    public override PaintRecipe Resolve(ExpressionScope scope)
    {
        if (Expressions is null)
        {
            return this;
        }

        var alpha = PaintOpacity.Resolve(scope, Expressions.ConstantOpacity, Expressions.Opacity, "circle-opacity");
        return this with
        {
            Fill = PaintExpressions.Tinted(scope, Expressions.Color, alpha, "circle-color", Fill),
            Radius = PaintExpressions.Size(scope, Expressions.Radius, "circle-radius", Radius),
            Stroke = PaintExpressions.Tinted(scope, Expressions.StrokeColor, alpha, "circle-stroke-color", Stroke),
            StrokeWidth = PaintExpressions.Size(scope, Expressions.StrokeWidth, "circle-stroke-width", StrokeWidth),
            Expressions = null,
        };
    }
}

/// <summary>
/// Resolves a data-driven recipe's alpha: the authored constant when the
/// opacity is not itself an expression, otherwise the opacity resolved for
/// this feature.
/// </summary>
internal static class PaintOpacity
{
    public static double Resolve(
        ExpressionScope scope, double constantOpacity, StyleExpression? opacity, string property) =>
        opacity is null ? constantOpacity : PaintExpressions.Number(scope, opacity, property, constantOpacity);
}

/// <summary>Where a label's box sits relative to its anchor point (MapLibre's <c>text-anchor</c>).</summary>
internal enum SymbolAnchor
{
    Center,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// A compiled symbol recipe: the text template and font request, the label
/// colour/halo, the anchor/offset/padding, and the optional sprite icon. The
/// per-feature text and icon are resolved from attributes by the scene builder;
/// this record is style-only and free of Skia types.
/// </summary>
internal sealed record SymbolOptions(
    string TextField,
    IReadOnlyList<string> Fonts,
    double Size,
    StyleColor Color,
    StyleColor HaloColor,
    double HaloWidth,
    SymbolAnchor Anchor,
    double OffsetX,
    double OffsetY,
    double Padding,
    bool AllowTextOverlap,
    string? IconImage,
    double IconSize,
    bool AllowIconOverlap);

internal sealed record SymbolPaint(SymbolOptions Options, SymbolExpressions? Expressions = null) : PaintRecipe
{
    public override bool IsDataDriven() => Expressions is not null;

    public override PaintRecipe Resolve(ExpressionScope scope)
    {
        if (Expressions is null)
        {
            return this;
        }

        var alpha = PaintOpacity.Resolve(scope, Expressions.ConstantOpacity, Expressions.Opacity, "text-opacity");
        return this with
        {
            Options = Options with
            {
                Color = PaintExpressions.Tinted(scope, Expressions.Color, alpha, "text-color", Options.Color),
                HaloColor = PaintExpressions.Tinted(
                    scope, Expressions.HaloColor, alpha, "text-halo-color", Options.HaloColor),
                HaloWidth = PaintExpressions.Size(scope, Expressions.HaloWidth, "text-halo-width", Options.HaloWidth),
            },
            Expressions = null,
        };
    }
}

/// <summary>One compiled style layer: identity, dataset key, zoom window, predicate and paint.</summary>
internal sealed record DrawLayer(
    string Id,
    string? Dataset,
    DrawKind Kind,
    double MinZoom,
    double MaxZoom,
    bool Visible,
    StyleFilter Filter,
    PaintRecipe Paint)
{
    /// <summary>Whether the layer is visible at <paramref name="zoom"/> (inclusive lower, exclusive upper).</summary>
    public bool IsVisibleAt(double zoom) => Visible && zoom >= MinZoom && zoom < MaxZoom;
}

/// <summary>A compiled style document: layers in document order (bottom to top).</summary>
internal sealed record CompiledStyle(IReadOnlyList<DrawLayer> Layers);
