using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>One symbol layer's drawing state, shared across its features.</summary>
internal sealed record SymbolDrawContext(
    SKCanvas Canvas,
    SymbolOptions Options,
    FontSession Fonts,
    SpriteRegistry Sprites);

/// <summary>
/// Draws the placements the placement pass decided (ADR-0080). Drawing is
/// separate from placing so priority can order placement across layers while
/// the pixels keep the style's document order. A label is drawn in its
/// candidate's frame, so a line label runs along its line and <c>text-rotate</c>
/// turns the rest; a frame with no rotation draws at the same coordinates as
/// before, which is what keeps the committed goldens byte-identical.
/// </summary>
internal static class SkiaSymbolRasterizer
{
    /// <summary>Draws one layer's placed features, in the layer's own feature order.</summary>
    public static void Draw(
        SymbolDrawContext context, IReadOnlyList<SymbolFeature> features, IReadOnlyList<PlacedSymbol?> placed)
    {
        for (var index = 0; index < features.Count; index++)
        {
            // A data-driven symbol paint resolves per feature; the font and the
            // size stay layer-level because they are layout, not paint.
            var options = features[index].Options ?? context.Options;
            if (index >= placed.Count || placed[index] is not { } placement || !placement.DrawsAnything)
            {
                continue;
            }

            Draw(context, features[index], options, placement);
        }
    }

    private static void Draw(
        SymbolDrawContext context, SymbolFeature feature, SymbolOptions options, PlacedSymbol placement)
    {
        var candidate = placement.Candidate;
        var degrees = (float)(candidate.Degrees + options.Rotate);
        var rotated = degrees != 0;
        var bundled = context.Fonts.Resolve(options.Fonts);
        using var font = BundledFont.CreateFont(bundled.Face, options.Size);
        var label = placement.Text is null
            ? null
            : ShapedLabel.Shape(feature.Text!, font, font.Metrics, bundled.Shaper, options);

        context.Canvas.Save();
        try
        {
            if (rotated)
            {
                context.Canvas.Translate(candidate.X, candidate.Y);
                context.Canvas.RotateDegrees(degrees);
            }

            // An unrotated frame draws in canvas pixels, so the anchor is added
            // back to the local box; a rotated one is already in its own frame.
            var (offsetX, offsetY) = rotated ? (0f, 0f) : (candidate.X, candidate.Y);
            if (placement.Icon is { } icon)
            {
                DrawIcon(context, feature.Icon!, options, icon, offsetX, offsetY);
            }

            if (label is not null && placement.Text is { } text)
            {
                DrawText(context, options, label, text, offsetX, offsetY, bundled);
            }
        }
        finally
        {
            context.Canvas.Restore();
        }
    }

    private static void DrawText(
        SymbolDrawContext context,
        SymbolOptions options,
        ShapedLabel label,
        SymbolBox box,
        float offsetX,
        float offsetY,
        BundledFont bundled)
    {
        using var font = BundledFont.CreateFont(bundled.Face, options.Size);
        var halo = options.HaloWidth > 0 && options.HaloColor.Alpha > 0;
        using var haloPaint = halo ? SkiaPaintFactory.TextStroke(options.HaloColor, options.HaloWidth) : null;
        using var fill = SkiaPaintFactory.TextFill(options.Color);

        for (var index = 0; index < label.Lines.Count; index++)
        {
            var line = label.Lines[index];
            if (line.IsEmpty)
            {
                continue;
            }

            var baseline = offsetY + box.Top - font.Metrics.Ascent + (float)(index * label.LineAdvance);
            if (haloPaint is not null)
            {
                DrawLine(context, bundled, line, offsetX + box.Left, baseline, font, haloPaint);
            }

            DrawLine(context, bundled, line, offsetX + box.Left, baseline, font, fill);
        }
    }

    /// <summary>
    /// Draws one shaped line. An untracked line is a single shaped run, which
    /// is the path the committed goldens were blessed on; a tracked line draws
    /// its runs at their tracked origins, so the extra advance lands between
    /// them and the shaping is still HarfBuzz's.
    /// </summary>
    private static void DrawLine(
        SymbolDrawContext context, BundledFont bundled, ShapedLine line, float left, float baseline, SKFont font, SKPaint paint)
    {
        foreach (var run in line.Runs)
        {
            context.Canvas.DrawShapedText(
                bundled.Shaper, run.Slice(line.Text), left + run.X, baseline, SKTextAlign.Left, font, paint);
        }
    }

    private static void DrawIcon(
        SymbolDrawContext context, string icon, SymbolOptions options, SymbolBox box, float offsetX, float offsetY)
    {
        var picture = context.Sprites.Get(icon);
        var cull = picture.CullRect;
        var scale = (float)options.IconSize;
        context.Canvas.Save();
        context.Canvas.Translate(offsetX + box.Left - (cull.Left * scale), offsetY + box.Top - (cull.Top * scale));
        context.Canvas.Scale(scale);
        context.Canvas.DrawPicture(picture);
        context.Canvas.Restore();
    }
}
