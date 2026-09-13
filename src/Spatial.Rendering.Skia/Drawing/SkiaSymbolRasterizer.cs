using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>One symbol layer's drawing state, shared across its features.</summary>
internal sealed record SymbolDrawContext(
    SKCanvas Canvas,
    SymbolOptions Options,
    ViewportProjection Projection,
    List<SKRect> Collision,
    BundledFont Font,
    SpriteRegistry Sprites);

/// <summary>
/// Shapes, places and draws one symbol layer's labels and icons. Placement is
/// a deterministic greedy first-fit: candidates arrive in scene order and are
/// tested against the shared collision list, so repeated runs at the same
/// viewport produce the same boxes and the same skipped labels (ADR-0049).
/// </summary>
internal static class SkiaSymbolRasterizer
{
    private const float IconPadding = 2f;

    public static void Draw(SymbolDrawContext context, IReadOnlyList<SymbolFeature> features)
    {
        using var skFont = BundledFont.CreateFont(context.Options.Size);
        var metrics = skFont.Metrics;
        var hasText = !string.IsNullOrEmpty(context.Options.TextField);
        var halo = context.Options.HaloWidth > 0 && context.Options.HaloColor.Alpha > 0;

        foreach (var feature in features)
        {
            if (AnchorPoint(feature.Geometry, context.Projection) is not { } anchor)
            {
                continue;
            }

            PlaceIcon(context, feature.Icon, anchor);
            if (hasText && !string.IsNullOrEmpty(feature.Text))
            {
                PlaceText(context, feature.Text, skFont, metrics, anchor, halo);
            }
        }
    }

    private static void PlaceText(
        SymbolDrawContext context,
        string text,
        SKFont skFont,
        SKFontMetrics metrics,
        (float X, float Y) anchor,
        bool halo)
    {
        var shaped = context.Font.Shaper.Shape(text, skFont);
        var width = shaped.Width;
        var height = metrics.Descent - metrics.Ascent;
        var options = context.Options;
        var (left, top) = Align(
            anchor.X + (float)(options.OffsetX * options.Size),
            anchor.Y + (float)(options.OffsetY * options.Size),
            width,
            height,
            options.Anchor);

        if (!Place(context.Collision, new SKRect(left, top, left + width, top + height), options.Padding, options.AllowTextOverlap))
        {
            return;
        }

        var baseline = top - metrics.Ascent;
        if (halo)
        {
            using var haloPaint = SkiaPaintFactory.TextStroke(options.HaloColor, options.HaloWidth);
            context.Canvas.DrawShapedText(context.Font.Shaper, text, left, baseline, SKTextAlign.Left, skFont, haloPaint);
        }

        using var fill = SkiaPaintFactory.TextFill(options.Color);
        context.Canvas.DrawShapedText(context.Font.Shaper, text, left, baseline, SKTextAlign.Left, skFont, fill);
    }

    private static void PlaceIcon(SymbolDrawContext context, string? icon, (float X, float Y) anchor)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return;
        }

        var picture = context.Sprites.Get(icon);
        var cull = picture.CullRect;
        var scale = (float)context.Options.IconSize;
        var width = cull.Width * scale;
        var height = cull.Height * scale;
        var left = anchor.X - width / 2;
        var top = anchor.Y - height / 2;

        if (!Place(context.Collision, new SKRect(left, top, left + width, top + height), IconPadding, context.Options.AllowIconOverlap))
        {
            return;
        }

        context.Canvas.Save();
        context.Canvas.Translate(left - cull.Left * scale, top - cull.Top * scale);
        context.Canvas.Scale(scale);
        context.Canvas.DrawPicture(picture);
        context.Canvas.Restore();
    }

    /// <summary>Adds the padded box to the collision list when it is free, and reports whether it was placed.</summary>
    private static bool Place(List<SKRect> collision, SKRect box, double padding, bool allowOverlap)
    {
        var padded = box;
        padded.Inflate((float)padding, (float)padding);
        if (!allowOverlap && collision.Any(existing => existing.IntersectsWith(padded)))
        {
            return false;
        }

        collision.Add(padded);
        return true;
    }

    private static (float Left, float Top) Align(float anchorX, float anchorY, float width, float height, SymbolAnchor anchor)
    {
        var left = anchor switch
        {
            SymbolAnchor.Left or SymbolAnchor.TopLeft or SymbolAnchor.BottomLeft => anchorX,
            SymbolAnchor.Right or SymbolAnchor.TopRight or SymbolAnchor.BottomRight => anchorX - width,
            _ => anchorX - width / 2,
        };
        var top = anchor switch
        {
            SymbolAnchor.Top or SymbolAnchor.TopLeft or SymbolAnchor.TopRight => anchorY,
            SymbolAnchor.Bottom or SymbolAnchor.BottomLeft or SymbolAnchor.BottomRight => anchorY - height,
            _ => anchorY - height / 2,
        };
        return (left, top);
    }

    /// <summary>
    /// The feature's anchor in pixels: its point for point geometries, and the
    /// envelope centre for line/polygon geometry (documented in ADR-0049).
    /// </summary>
    private static (float X, float Y)? AnchorPoint(IGeometry geometry, ViewportProjection projection)
    {
        var coordinate = FirstPoint(geometry) ?? Centre(geometry);
        return coordinate is { } value ? projection.ToPixel(value.X, value.Y) : null;
    }

    private static Coordinate? Centre(IGeometry geometry) =>
        geometry.Envelope is { } envelope
            ? new Coordinate((envelope.MinX + envelope.MaxX) / 2, (envelope.MinY + envelope.MaxY) / 2)
            : null;

    private static Coordinate? FirstPoint(IGeometry geometry) => geometry switch
    {
        IPoint point => point.Coordinate,
        IMultiPoint multi => multi.Points.Select(child => child.Coordinate).FirstOrDefault(coordinate => coordinate is not null),
        _ => null,
    };
}
