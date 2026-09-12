namespace Spatial.Rendering.Skia.Styling;

/// <summary>The layer kinds the documented MapLibre subset supports.</summary>
internal enum DrawKind
{
    Background,
    Fill,
    Line,
    Circle,
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
internal abstract record PaintRecipe;

internal sealed record BackgroundPaint(StyleColor Color) : PaintRecipe;

internal sealed record FillPaint(StyleColor Fill, StyleColor Outline, double OutlineWidth) : PaintRecipe;

internal sealed record LinePaint(
    StyleColor Stroke,
    double Width,
    IReadOnlyList<double> Dash,
    LineCapStyle Cap,
    LineJoinStyle Join) : PaintRecipe;

internal sealed record CirclePaint(StyleColor Fill, double Radius, StyleColor Stroke, double StrokeWidth) : PaintRecipe;

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
