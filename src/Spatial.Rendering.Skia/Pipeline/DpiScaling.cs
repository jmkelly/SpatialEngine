using Spatial.Contracts;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Scales compiled paint sizes for a request DPI (T-045 item 3): style pixel
/// sizes (line widths and dashes, circle radii, text and halo sizes, offsets,
/// padding and icon sizes) are defined at
/// <see cref="MapRenderRequest.ReferenceDpi"/> and grow linearly with the
/// request DPI, so a WMS GetMap at 192 dpi prints the same physical sizes as
/// the 96-dpi authoring. Geometry placement and the output frame are
/// untouched — only symbology scales. The transform runs after the compiler
/// cache, so cached plans are never mutated.
/// </summary>
internal static class DpiScaling
{
    public static bool NeedsScaling(double dpi) =>
        double.IsFinite(dpi) && Math.Abs(dpi / MapRenderRequest.ReferenceDpi - 1) > 1e-9;

    public static CompiledStyle Apply(CompiledStyle style, double dpi)
    {
        ArgumentNullException.ThrowIfNull(style);
        var ratio = dpi / MapRenderRequest.ReferenceDpi;
        return new CompiledStyle(style.Layers.Select(layer => layer with { Paint = Scale(layer.Paint, ratio) }).ToArray());
    }

    private static readonly Dictionary<Type, Func<PaintRecipe, double, PaintRecipe>> Scalers = new()
    {
        [typeof(BackgroundPaint)] = (paint, _) => paint,
        [typeof(FillPaint)] = (paint, ratio) => ScaleFill((FillPaint)paint, ratio),
        [typeof(LinePaint)] = (paint, ratio) => ScaleLine((LinePaint)paint, ratio),
        [typeof(CirclePaint)] = (paint, ratio) => ScaleCircle((CirclePaint)paint, ratio),
        [typeof(SymbolPaint)] = (paint, ratio) => ScaleSymbol((SymbolPaint)paint, ratio),
    };

    private static PaintRecipe Scale(PaintRecipe paint, double ratio) =>
        Scalers.TryGetValue(paint.GetType(), out var scale) ? scale(paint, ratio) : paint;

    private static FillPaint ScaleFill(FillPaint fill, double ratio) =>
        fill with { OutlineWidth = fill.OutlineWidth * ratio };

    private static LinePaint ScaleLine(LinePaint line, double ratio) => line with
    {
        Width = line.Width * ratio,
        Dash = line.Dash.Select(dash => dash * ratio).ToArray(),
    };

    private static CirclePaint ScaleCircle(CirclePaint circle, double ratio) => circle with
    {
        Radius = circle.Radius * ratio,
        StrokeWidth = circle.StrokeWidth * ratio,
    };

    private static SymbolPaint ScaleSymbol(SymbolPaint symbol, double ratio) =>
        symbol with { Options = Scale(symbol.Options, ratio) };

    private static SymbolOptions Scale(SymbolOptions options, double ratio) => options with
    {
        Size = options.Size * ratio,
        HaloWidth = options.HaloWidth * ratio,
        OffsetX = options.OffsetX * ratio,
        OffsetY = options.OffsetY * ratio,
        Padding = options.Padding * ratio,
        IconSize = options.IconSize * ratio,
    };
}
