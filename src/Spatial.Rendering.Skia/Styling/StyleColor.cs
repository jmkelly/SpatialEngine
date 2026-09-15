using System.Globalization;
using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// A straight (non-premultiplied) RGBA colour in the compiled style model.
/// Kept free of Skia types so the style compiler is testable without touching
/// the native rasterizer surface.
/// </summary>
internal readonly record struct StyleColor(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    public static readonly StyleColor Transparent = new(0, 0, 0, 0);

    /// <summary>Multiplies the alpha channel by <paramref name="opacity"/> (clamped to [0, 1]).</summary>
    public StyleColor ScaleAlpha(double opacity)
    {
        var alpha = Math.Clamp(Alpha * opacity, 0, 255);
        return this with { Alpha = (byte)Math.Round(alpha, MidpointRounding.AwayFromZero) };
    }
}
