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

/// <summary>Where candidates are generated for a symbol feature (MapLibre's <c>symbol-placement</c>).</summary>
internal enum SymbolPlacement
{
    /// <summary>One candidate at the feature's point, envelope centre or an anchor offset from it.</summary>
    Point,

    /// <summary>Repeated candidates along a line geometry, each aligned to the local line direction.</summary>
    Line,
}

/// <summary>The transform applied to a label's shaped text (MapLibre's <c>text-transform</c>).</summary>
internal enum SymbolTextTransform
{
    None,
    Uppercase,
    Lowercase,
}

/// <summary>
/// A compiled symbol recipe: the text template and font request, the label
/// colour/halo, the anchor/offset/padding, the optional sprite icon, the
/// placement mode with its candidate spacing and priority, and the text
/// transforms. The per-feature text and icon are resolved from attributes by
/// the scene builder; this record is style-only and free of Skia types.
/// </summary>
internal sealed record SymbolOptions
{
    /// <summary>The <c>{token}</c> template, resolved per feature.</summary>
    public string TextField { get; init; } = string.Empty;

    /// <summary>
    /// The requested face names, in fallback order. Never validated here: a
    /// family the bundle does not carry resolves down the documented chain at
    /// draw time rather than failing the style.
    /// </summary>
    public IReadOnlyList<string> Fonts { get; init; } = [];

    public double Size { get; init; } = 16;

    public StyleColor Color { get; init; } = new(0, 0, 0);

    public StyleColor HaloColor { get; init; } = StyleColor.Transparent;

    public double HaloWidth { get; init; }

    public SymbolAnchor Anchor { get; init; } = SymbolAnchor.Center;

    /// <summary>The offset in ems, in the candidate's own frame (so a line label offsets perpendicular).</summary>
    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public double Padding { get; init; } = 2;

    public bool AllowTextOverlap { get; init; }

    public string? IconImage { get; init; }

    public double IconSize { get; init; } = 1;

    public bool AllowIconOverlap { get; init; }

    /// <summary>Where candidates are generated for a feature.</summary>
    public SymbolPlacement Placement { get; init; } = SymbolPlacement.Point;

    /// <summary>The pixels between line-placement candidates along a line geometry.</summary>
    public double Spacing { get; init; } = 250;

    /// <summary>
    /// The style's placement priority: a lower value is placed first and so
    /// wins a collision. The identity/envelope-centre tie-break stays the
    /// final key, which keeps the order a total one.
    /// </summary>
    public double SortKey { get; init; }

    /// <summary>
    /// <c>symbol-allow-overlap</c>. Null means "inherit", i.e. the per-component
    /// <c>text-</c>/<c>icon-allow-overlap</c> flags, which is the MapLibre default.
    /// </summary>
    public bool? AllowOverlap { get; init; }

    /// <summary>
    /// <c>symbol-ignore-placement</c>: place every feature in priority order
    /// without a collision test, but still in that order.
    /// </summary>
    public bool IgnorePlacement { get; init; }

    public SymbolTextTransform TextTransform { get; init; } = SymbolTextTransform.None;

    /// <summary>Extra tracking between glyphs, in ems.</summary>
    public double LetterSpacing { get; init; }

    /// <summary>The line advance between the lines of a multi-line label, in ems.</summary>
    public double LineHeight { get; init; } = 1.2;

    /// <summary>A fixed rotation in degrees applied to the text about its anchor.</summary>
    public double Rotate { get; init; }

    /// <summary>The effective overlap bypass: the explicit flag, or the per-component flags when it is unset.</summary>
    public bool AllowsOverlap => AllowOverlap ?? (AllowTextOverlap || AllowIconOverlap);
}

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
