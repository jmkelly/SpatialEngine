using System.Text.Json;
using Spatial.Contracts;

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

    public static SymbolPaint Read(JsonElement layout, JsonElement paint, ExpressionInterner? interner = null)
    {
        var layoutBag = new StylePropertyBag(layout, "layout", interner);
        var paintBag = new StylePropertyBag(paint, "paint", interner);

        var (opacity, opacityExpression) = paintBag.Number("text-opacity", 1.0);
        var (color, colorExpression) = paintBag.Color("text-color", new StyleColor(0, 0, 0));
        var (haloColor, haloColorExpression) = paintBag.Color("text-halo-color", StyleColor.Transparent);
        var (haloWidth, haloWidthExpression) = paintBag.Number("text-halo-width", 0.0);
        paintBag.RejectUnsupported();

        // 'visibility' is consumed and applied by the compiler; accept it here
        // so the layout bag does not report it as unsupported.
        layoutBag.String("visibility", "visible");
        var textField = layoutBag.String("text-field", null);
        var fonts = layoutBag.StringList("text-font");
        var size = layoutBag.ConstantNumber("text-size", 16.0);
        var anchor = ReadAnchor(layoutBag.String("text-anchor", "center"));
        var (offsetX, offsetY) = layoutBag.NumberPair("text-offset", (0.0, 0.0));
        var padding = layoutBag.ConstantNumber("text-padding", 2.0);
        var allowTextOverlap = layoutBag.Bool("text-allow-overlap", false);
        var iconImage = layoutBag.String("icon-image", null);
        var iconSize = layoutBag.ConstantNumber("icon-size", 1.0);
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
        var dataDriven = colorExpression is not null || opacityExpression is not null
            || haloColorExpression is not null || haloWidthExpression is not null;
        var alpha = dataDriven ? 1.0 : opacity;
        return new SymbolPaint(new SymbolOptions(
            textField ?? string.Empty,
            fonts,
            size,
            color.ScaleAlpha(alpha),
            haloColor.ScaleAlpha(alpha),
            haloWidth,
            anchor,
            offsetX,
            offsetY,
            padding,
            allowTextOverlap,
            iconImage,
            iconSize,
            allowIconOverlap),
            dataDriven
                ? new SymbolExpressions(colorExpression, opacityExpression, haloColorExpression, haloWidthExpression, opacity)
                : null);
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
