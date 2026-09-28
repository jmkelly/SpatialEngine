using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads a <c>symbol</c> layer's <c>layout</c> and <c>paint</c> into the
/// internal <see cref="SymbolPaint"/>. The documented MapLibre subset is
/// text labels over the bundled font family plus optional embedded SVG icons;
/// an unsupported key is a typed <c>invalid.arguments</c> naming it.
/// </summary>
internal static class SymbolReader
{
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

    private static readonly Dictionary<string, SymbolTextTransform> Transforms = new(StringComparer.Ordinal)
    {
        ["none"] = SymbolTextTransform.None,
        ["uppercase"] = SymbolTextTransform.Uppercase,
        ["lowercase"] = SymbolTextTransform.Lowercase,
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
        var placement = ReadPlacement(layoutBag.String("symbol-placement", "point"));
        var spacing = layoutBag.ConstantNumber("symbol-spacing", 250.0);
        var sortKey = layoutBag.ConstantNumber("symbol-sort-key", 0.0);
        var symbolAllowOverlap = layoutBag.OptionalBool("symbol-allow-overlap");
        var ignorePlacement = layoutBag.Bool("symbol-ignore-placement", false);
        var textTransform = ReadTransform(layoutBag.String("text-transform", "none"));
        var letterSpacing = layoutBag.ConstantNumber("text-letter-spacing", 0.0);
        var lineHeight = layoutBag.ConstantNumber("text-line-height", 1.2);
        var rotate = layoutBag.ConstantNumber("text-rotate", 0.0);
        layoutBag.RejectUnsupported();

        if (string.IsNullOrEmpty(textField) && string.IsNullOrEmpty(iconImage))
        {
            throw SpatialException.BadArguments("A 'symbol' layer requires 'text-field' or 'icon-image'.");
        }

        PaintReader.AssertNonNegative(size, "text-size");
        PaintReader.AssertNonNegative(padding, "text-padding");
        PaintReader.AssertNonNegative(haloWidth, "text-halo-width");
        PaintReader.AssertNonNegative(iconSize, "icon-size");
        PaintReader.AssertNonNegative(spacing, "symbol-spacing");
        PaintReader.AssertNonNegative(letterSpacing, "text-letter-spacing");
        if (spacing == 0)
        {
            throw SpatialException.BadArguments("Unsupported value for 'symbol-spacing': expected a positive number.");
        }

        if (lineHeight <= 0)
        {
            throw SpatialException.BadArguments("Unsupported value for 'text-line-height': expected a positive number.");
        }

        var dataDriven = colorExpression is not null || opacityExpression is not null
            || haloColorExpression is not null || haloWidthExpression is not null;
        var alpha = dataDriven ? 1.0 : opacity;
        return new SymbolPaint(new SymbolOptions
        {
            TextField = textField ?? string.Empty,
            Fonts = fonts,
            Size = size,
            Color = color.ScaleAlpha(alpha),
            HaloColor = haloColor.ScaleAlpha(alpha),
            HaloWidth = haloWidth,
            Anchor = anchor,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Padding = padding,
            AllowTextOverlap = allowTextOverlap,
            IconImage = iconImage,
            IconSize = iconSize,
            AllowIconOverlap = allowIconOverlap,
            Placement = placement,
            Spacing = spacing,
            SortKey = sortKey,
            AllowOverlap = symbolAllowOverlap,
            IgnorePlacement = ignorePlacement,
            TextTransform = textTransform,
            LetterSpacing = letterSpacing,
            LineHeight = lineHeight,
            Rotate = rotate,
        },
            dataDriven
                ? new SymbolExpressions(colorExpression, opacityExpression, haloColorExpression, haloWidthExpression, opacity)
                : null);
    }

    private static SymbolPlacement ReadPlacement(string? placement) =>
        placement switch
        {
            null or "point" => SymbolPlacement.Point,
            "line" => SymbolPlacement.Line,
            _ => throw SpatialException.BadArguments($"Unsupported value for 'symbol-placement': '{placement}'."),
        };

    private static SymbolTextTransform ReadTransform(string? transform) =>
        transform is not null && Transforms.TryGetValue(transform, out var value)
            ? value
            : throw SpatialException.BadArguments($"Unsupported value for 'text-transform': '{transform}'.");

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
