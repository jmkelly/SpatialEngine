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

        var paintValues = ReadPaint(paintBag);
        paintBag.RejectUnsupported();

        // 'visibility' is consumed and applied by the compiler; accept it here
        // so the layout bag does not report it as unsupported.
        layoutBag.String("visibility", "visible");
        var layoutValues = ReadLayout(layoutBag);
        layoutBag.RejectUnsupported();

        ValidateLayout(layoutValues, paintValues.HaloWidth);
        return Build(paintValues, layoutValues);
    }

    /// <summary>The paint values a symbol layer carries: constants plus their data-driven expressions.</summary>
    private sealed record PaintValues(
        double Opacity,
        StyleExpression? OpacityExpression,
        StyleColor Color,
        StyleExpression? ColorExpression,
        StyleColor HaloColor,
        StyleExpression? HaloColorExpression,
        double HaloWidth,
        StyleExpression? HaloWidthExpression);

    /// <summary>The layout values a symbol layer carries, as read before validation.</summary>
    private sealed record LayoutValues(
        string? TextField,
        List<string> Fonts,
        double Size,
        SymbolAnchor Anchor,
        double OffsetX,
        double OffsetY,
        double Padding,
        bool AllowTextOverlap,
        string? IconImage,
        double IconSize,
        bool AllowIconOverlap,
        SymbolPlacement Placement,
        double Spacing,
        double SortKey,
        bool? AllowOverlap,
        bool IgnorePlacement,
        SymbolTextTransform TextTransform,
        double LetterSpacing,
        double LineHeight,
        double Rotate);

    private static PaintValues ReadPaint(StylePropertyBag paintBag)
    {
        var (opacity, opacityExpression) = paintBag.Number("text-opacity", 1.0);
        var (color, colorExpression) = paintBag.Color("text-color", new StyleColor(0, 0, 0));
        var (haloColor, haloColorExpression) = paintBag.Color("text-halo-color", StyleColor.Transparent);
        var (haloWidth, haloWidthExpression) = paintBag.Number("text-halo-width", 0.0);
        return new PaintValues(
            opacity, opacityExpression, color, colorExpression,
            haloColor, haloColorExpression, haloWidth, haloWidthExpression);
    }

    private static LayoutValues ReadLayout(StylePropertyBag layoutBag)
    {
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
        return new LayoutValues(
            textField, [.. fonts], size, anchor, offsetX, offsetY, padding,
            allowTextOverlap, iconImage, iconSize, allowIconOverlap, placement,
            spacing, sortKey, symbolAllowOverlap, ignorePlacement, textTransform,
            letterSpacing, lineHeight, rotate);
    }

    /// <summary>The range checks a symbol layer must pass: non-negative sizes and positive spacing and line height.</summary>
    private static void ValidateLayout(LayoutValues layout, double haloWidth)
    {
        if (string.IsNullOrEmpty(layout.TextField) && string.IsNullOrEmpty(layout.IconImage))
        {
            throw SpatialException.BadArguments("A 'symbol' layer requires 'text-field' or 'icon-image'.");
        }

        PaintReader.AssertNonNegative(layout.Size, "text-size");
        PaintReader.AssertNonNegative(layout.Padding, "text-padding");
        PaintReader.AssertNonNegative(haloWidth, "text-halo-width");
        PaintReader.AssertNonNegative(layout.IconSize, "icon-size");
        PaintReader.AssertNonNegative(layout.Spacing, "symbol-spacing");
        PaintReader.AssertNonNegative(layout.LetterSpacing, "text-letter-spacing");
        AssertPositiveSpacing(layout.Spacing);
        AssertPositiveLineHeight(layout.LineHeight);
    }

    private static void AssertPositiveSpacing(double spacing)
    {
        if (spacing == 0)
        {
            throw SpatialException.BadArguments("Unsupported value for 'symbol-spacing': expected a positive number.");
        }
    }

    private static void AssertPositiveLineHeight(double lineHeight)
    {
        if (lineHeight <= 0)
        {
            throw SpatialException.BadArguments("Unsupported value for 'text-line-height': expected a positive number.");
        }
    }

    private static SymbolPaint Build(PaintValues paint, LayoutValues layout)
    {
        var dataDriven = paint.ColorExpression is not null || paint.OpacityExpression is not null
            || paint.HaloColorExpression is not null || paint.HaloWidthExpression is not null;
        var alpha = dataDriven ? 1.0 : paint.Opacity;
        return new SymbolPaint(new SymbolOptions
        {
            TextField = layout.TextField ?? string.Empty,
            Fonts = layout.Fonts,
            Size = layout.Size,
            Color = paint.Color.ScaleAlpha(alpha),
            HaloColor = paint.HaloColor.ScaleAlpha(alpha),
            HaloWidth = paint.HaloWidth,
            Anchor = layout.Anchor,
            OffsetX = layout.OffsetX,
            OffsetY = layout.OffsetY,
            Padding = layout.Padding,
            AllowTextOverlap = layout.AllowTextOverlap,
            IconImage = layout.IconImage,
            IconSize = layout.IconSize,
            AllowIconOverlap = layout.AllowIconOverlap,
            Placement = layout.Placement,
            Spacing = layout.Spacing,
            SortKey = layout.SortKey,
            AllowOverlap = layout.AllowOverlap,
            IgnorePlacement = layout.IgnorePlacement,
            TextTransform = layout.TextTransform,
            LetterSpacing = layout.LetterSpacing,
            LineHeight = layout.LineHeight,
            Rotate = layout.Rotate,
        },
            dataDriven
                ? new SymbolExpressions(
                    paint.ColorExpression, paint.OpacityExpression,
                    paint.HaloColorExpression, paint.HaloWidthExpression, paint.Opacity)
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
