using SkiaSharp;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>Maps the compiled style's colours and stroke styles to Skia paints.</summary>
internal static class SkiaPaintFactory
{
    public static SKPaint Fill(StyleColor color) => new()
    {
        Style = SKPaintStyle.Fill,
        Color = ToSkia(color),
        IsAntialias = true,
    };

    public static SKPaint Stroke(StyleColor color, double width)
    {
        var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ToSkia(color),
            StrokeWidth = (float)width,
            IsAntialias = true,
        };
        return paint;
    }

    public static SKPaint Stroke(StyleColor color, double width, LineCapStyle cap, LineJoinStyle join)
    {
        var paint = Stroke(color, width);
        paint.StrokeCap = ToSkia(cap);
        paint.StrokeJoin = ToSkia(join);
        return paint;
    }

    public static SKPaint TextFill(StyleColor color) => new()
    {
        Style = SKPaintStyle.Fill,
        Color = ToSkia(color),
        IsAntialias = true,
    };

    public static SKPaint TextStroke(StyleColor color, double width) => new()
    {
        Style = SKPaintStyle.Stroke,
        Color = ToSkia(color),
        StrokeWidth = (float)width,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true,
    };

    public static SKColor ToSkia(StyleColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);

    public static SKStrokeCap ToSkia(LineCapStyle cap) => cap switch
    {
        LineCapStyle.Butt => SKStrokeCap.Butt,
        LineCapStyle.Round => SKStrokeCap.Round,
        _ => SKStrokeCap.Square,
    };

    public static SKStrokeJoin ToSkia(LineJoinStyle join) => join switch
    {
        LineJoinStyle.Miter => SKStrokeJoin.Miter,
        LineJoinStyle.Round => SKStrokeJoin.Round,
        _ => SKStrokeJoin.Bevel,
    };
}
