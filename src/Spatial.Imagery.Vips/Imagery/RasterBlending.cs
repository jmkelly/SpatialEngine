using NetVips;
using Spatial.Contracts;

namespace Spatial.Imagery.Vips.Imagery;

/// <summary>Maps contract blend modes to libvips and applies per-layer opacity to the alpha band.</summary>
internal static class RasterBlending
{
    private static readonly Dictionary<RasterBlend, Enums.BlendMode> VipsModes = new()
    {
        [RasterBlend.Over] = Enums.BlendMode.Over,
        [RasterBlend.Multiply] = Enums.BlendMode.Multiply,
        [RasterBlend.Screen] = Enums.BlendMode.Screen,
        [RasterBlend.Darken] = Enums.BlendMode.Darken,
        [RasterBlend.Lighten] = Enums.BlendMode.Lighten,
    };

    public static Enums.BlendMode ToVips(RasterBlend blend) =>
        VipsModes.TryGetValue(blend, out var mode)
            ? mode
            : throw SpatialException.BadArguments($"Unsupported blend mode '{blend}'.");

    /// <summary>Returns a new image whose alpha band is scaled by <paramref name="opacity"/>.</summary>
    public static Image ApplyOpacity(Image image, double opacity)
    {
        if (opacity is < 0 or > 1)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Layer opacity must be within [0, 1], got {opacity}."));
        }

        if (Math.Abs(opacity - 1) < 1e-9)
        {
            return image.Copy();
        }

        using var alpha = image.ExtractBand(image.Bands - 1).Linear([opacity], [0]);
        using var rgb = image.ExtractBand(0, image.Bands - 1);
        return rgb.Bandjoin(alpha);
    }
}
