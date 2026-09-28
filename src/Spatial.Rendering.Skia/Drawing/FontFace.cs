using SkiaSharp;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>The slant of a bundled face (MapLibre's font style).</summary>
internal enum FontStyle
{
    Normal,
    Italic,
}

/// <summary>
/// One bundled face: a family, a CSS weight (100-900), a style, and the
/// embedded resource that carries it with its pinned digest (ADR-0075). The
/// resource is an assembly stream, never the host font manager, so the glyphs
/// a label is shaped from are the same on every machine.
/// </summary>
internal sealed record FontFace(string Family, int Weight, FontStyle Style, string Resource, string Sha256)
{
    public const int RegularWeight = 400;
    public const int BoldWeight = 700;

    /// <summary>The name a style would write for this face, e.g. <c>Noto Sans Bold Italic</c>.</summary>
    public string Name => Style == FontStyle.Normal && Weight == RegularWeight
        ? $"{Family} Regular"
        : Style == FontStyle.Normal
            ? $"{Family} {WeightName}"
            : Weight == RegularWeight ? $"{Family} Italic" : $"{Family} {WeightName} Italic";

    private string WeightName => Weight switch
    {
        100 => "Thin",
        200 => "Extra Light",
        300 => "Light",
        400 => "Regular",
        500 => "Medium",
        600 => "SemiBold",
        700 => "Bold",
        800 => "Extra Bold",
        900 => "Black",
        _ => Weight.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The Skia slant for this face's style.</summary>
    public SKFontStyleSlant Slant => Style == FontStyle.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
}
