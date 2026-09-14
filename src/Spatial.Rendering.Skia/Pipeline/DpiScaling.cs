using Spatial.PluginSdk;
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

    private static PaintRecipe Scale(PaintRecipe paint, double ratio) => paint switch
    {
        BackgroundPaint => paint,
        FillPaint fill => fill with { OutlineWidth = fill.OutlineWidth * ratio },
        LinePaint line => line with
        {
            Width = line.Width * ratio,
            Dash = line.Dash.Select(dash => dash * ratio).ToArray(),
        },
        CirclePaint circle => circle with
        {
            Radius = circle.Radius * ratio,
            StrokeWidth = circle.StrokeWidth * ratio,
        },
        SymbolPaint symbol => symbol with { Options = Scale(symbol.Options, ratio) },
        _ => paint,
    };

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
