using NetVips;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Maps NetVips band formats to and from the core <see cref="RasterPixelType"/>
/// vocabulary (ADR-0051). Every NetVips type stays inside this assembly; the
/// SDK only ever sees the core enum.
/// </summary>
internal static class RasterBandFormats
{
    /// <summary>The core pixel type for every NetVips band format the engine carries.</summary>
    private static readonly Dictionary<Enums.BandFormat, RasterPixelType> CoreByBandFormat =
        new Dictionary<Enums.BandFormat, RasterPixelType>
        {
            [Enums.BandFormat.Uchar] = RasterPixelType.U8,
            [Enums.BandFormat.Char] = RasterPixelType.S8,
            [Enums.BandFormat.Ushort] = RasterPixelType.U16,
            [Enums.BandFormat.Short] = RasterPixelType.S16,
            [Enums.BandFormat.Uint] = RasterPixelType.U32,
            [Enums.BandFormat.Int] = RasterPixelType.S32,
            [Enums.BandFormat.Float] = RasterPixelType.F32,
            [Enums.BandFormat.Double] = RasterPixelType.F64,
            [Enums.BandFormat.Complex] = RasterPixelType.C64,
            [Enums.BandFormat.Dpcomplex] = RasterPixelType.C128,
        };

    /// <summary>The NetVips band format used for each core pixel type's cast.</summary>
    private static readonly Dictionary<RasterPixelType, Enums.BandFormat> BandFormatByCore =
        new Dictionary<RasterPixelType, Enums.BandFormat>
        {
            [RasterPixelType.U8] = Enums.BandFormat.Uchar,
            [RasterPixelType.S8] = Enums.BandFormat.Char,
            [RasterPixelType.U16] = Enums.BandFormat.Ushort,
            [RasterPixelType.S16] = Enums.BandFormat.Short,
            [RasterPixelType.U32] = Enums.BandFormat.Uint,
            [RasterPixelType.S32] = Enums.BandFormat.Int,
            [RasterPixelType.F32] = Enums.BandFormat.Float,
            [RasterPixelType.F64] = Enums.BandFormat.Double,
            [RasterPixelType.C64] = Enums.BandFormat.Complex,
            [RasterPixelType.C128] = Enums.BandFormat.Dpcomplex,
        };

    /// <summary>Maps a NetVips band format to the core pixel type.</summary>
    public static RasterPixelType ToCore(Enums.BandFormat format) =>
        CoreByBandFormat.TryGetValue(format, out var pixelType) ? pixelType : RasterPixelType.Unknown;

    /// <summary>Maps a core pixel type to the NetVips band format used for a cast.</summary>
    public static Enums.BandFormat ToVips(RasterPixelType pixelType) =>
        BandFormatByCore.TryGetValue(pixelType, out var format) ? format : Enums.BandFormat.Notset;

    /// <summary>
    /// Whether an export may cast to <paramref name="target"/> (ADR-0051
    /// §4): only the real integer and floating-point formats, never the
    /// complex or sub-byte ones, and a colour raster stays 8-bit.
    /// </summary>
    public static bool IsConvertible(int bandCount, RasterPixelType target)
    {
        if (target is not (RasterPixelType.U8 or RasterPixelType.U16 or RasterPixelType.S16
            or RasterPixelType.U32 or RasterPixelType.S32 or RasterPixelType.F32 or RasterPixelType.F64))
        {
            return false;
        }

        return bandCount < 3 || target == RasterPixelType.U8;
    }
}
