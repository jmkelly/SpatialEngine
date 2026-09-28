using System.Globalization;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// A straight (non-premultiplied) RGBA colour in the compiled style model.
/// Kept free of Skia types so the style compiler is testable without touching
/// the native rasterizer surface.
/// </summary>
internal readonly record struct StyleColor(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    public static readonly StyleColor Transparent = new(0, 0, 0, 0);

    /// <summary>The CSS hex form: <c>#rgb</c> is never produced, and the alpha byte is omitted when opaque.</summary>
    public string ToHex() =>
        Alpha == 255
            ? FormattableString.Invariant($"#{Red:X2}{Green:X2}{Blue:X2}")
            : FormattableString.Invariant($"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}");

    /// <summary>Multiplies the alpha channel by <paramref name="opacity"/> (clamped to [0, 1]).</summary>
    public StyleColor ScaleAlpha(double opacity)
    {
        var alpha = Math.Clamp(Alpha * opacity, 0, 255);
        return this with { Alpha = (byte)Math.Round(alpha, MidpointRounding.AwayFromZero) };
    }
}
