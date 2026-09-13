using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads a <c>symbol</c> layer's <c>layout</c> and <c>paint</c> into the
/// internal <see cref="SymbolPaint"/>. The documented MapLibre subset is
/// text labels over the bundled font plus optional embedded SVG icons; an
/// unsupported key is a typed <c>invalid.arguments</c> naming it.
/// </summary>
internal static class SymbolReader
{
    /// <summary>
    /// Font names accepted by <c>text-font</c>. The engine bundles one Regular
    /// face (Noto Sans, ADR-0049); the MapLibre default names are accepted as
    /// aliases to it so a vanilla style compiles, and any other name is
    /// rejected rather than silently substituted.
    /// </summary>
    private static readonly HashSet<string> SupportedFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Noto Sans",
        "Noto Sans Regular",
        "Open Sans",
        "Open Sans Regular",
        "Arial Unicode MS",
        "Arial Unicode MS Regular",
        "sans-serif",
    };

    private static readonly Dictionary<string, SymbolAnchor> Anchors = new(StringComparer.Ordinal)
    {
        ["center"] = SymbolAnchor.Center,
        ["left"] = SymbolAnchor.Left,
        ["right"] = SymbolAnchor.Right,
        ["top"] = SymbolAnchor.Top,
        ["bottom"] = SymbolAnchor.Bottom,
        ["top-left"] = SymbolAnchor.TopLeft,
        ["top-right"] = SymbolAnchor.TopRight,
        ["bottom-left"] = SymbolAnchor.BottomLeft,
        ["bottom-right"] = SymbolAnchor.BottomRight,
    };

    public static SymbolPaint Read(JsonElement layout, JsonElement paint)
    {
        var layoutBag = new StylePropertyBag(layout, "layout");
        var paintBag = new StylePropertyBag(paint, "paint");

        var opacity = paintBag.Number("text-opacity", 1.0);
        var color = paintBag.Color("text-color", new StyleColor(0, 0, 0)).ScaleAlpha(opacity);
        var haloColor = paintBag.Color("text-halo-color", StyleColor.Transparent).ScaleAlpha(opacity);
        var haloWidth = paintBag.Number("text-halo-width", 0.0);
        paintBag.RejectUnsupported();

        // 'visibility' is consumed and applied by the compiler; accept it here
        // so the layout bag does not report it as unsupported.
        layoutBag.String("visibility", "visible");
        var textField = layoutBag.String("text-field", null);
        var fonts = layoutBag.StringList("text-font");
        var size = layoutBag.Number("text-size", 16.0);
        var anchor = ReadAnchor(layoutBag.String("text-anchor", "center"));
        var (offsetX, offsetY) = layoutBag.NumberPair("text-offset", (0.0, 0.0));
        var padding = layoutBag.Number("text-padding", 2.0);
        var allowTextOverlap = layoutBag.Bool("text-allow-overlap", false);
        var iconImage = layoutBag.String("icon-image", null);
        var iconSize = layoutBag.Number("icon-size", 1.0);
        var allowIconOverlap = layoutBag.Bool("icon-allow-overlap", false);
        layoutBag.RejectUnsupported();

        if (string.IsNullOrEmpty(textField) && string.IsNullOrEmpty(iconImage))
        {
            throw SpatialException.BadArguments("A 'symbol' layer requires 'text-field' or 'icon-image'.");
        }

        AssertFonts(fonts);
        PaintReader.AssertNonNegative(size, "text-size");
        PaintReader.AssertNonNegative(padding, "text-padding");
        PaintReader.AssertNonNegative(haloWidth, "text-halo-width");
        PaintReader.AssertNonNegative(iconSize, "icon-size");
        return new SymbolPaint(new SymbolOptions(
            textField ?? string.Empty,
            fonts,
            size,
            color,
            haloColor,
            haloWidth,
            anchor,
            offsetX,
            offsetY,
            padding,
            allowTextOverlap,
            iconImage,
            iconSize,
            allowIconOverlap));
    }

    private static void AssertFonts(IReadOnlyList<string> fonts)
    {
        if (fonts.Count == 0)
        {
            return;
        }

        if (!fonts.Any(SupportedFonts.Contains))
        {
            throw SpatialException.BadArguments(
                $"Unsupported 'text-font' [{string.Join(", ", fonts)}]; the renderer bundles Noto Sans Regular.");
        }
    }

    private static SymbolAnchor ReadAnchor(string? text)
    {
        if (text is null)
        {
            return SymbolAnchor.Center;
        }

        return Anchors.TryGetValue(text, out var anchor)
            ? anchor
            : throw SpatialException.BadArguments($"Unsupported value for 'text-anchor': '{text}'.");
    }
}
