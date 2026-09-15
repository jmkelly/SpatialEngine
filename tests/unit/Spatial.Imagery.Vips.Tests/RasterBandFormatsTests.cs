using NetVips;
using Spatial.Contracts;
using Spatial.Imagery.Vips.Raster;

namespace Spatial.Imagery.Vips.Tests;

/// <summary>
/// The NetVips band-format ↔ core pixel-type vocabulary map (ADR-0051): every
/// supported pair round-trips and anything else maps to the unknown sentinel.
/// </summary>
public sealed class RasterBandFormatsTests
{
    public static TheoryData<Enums.BandFormat, RasterPixelType> BandFormats => new()
    {
        { Enums.BandFormat.Uchar, RasterPixelType.U8 },
        { Enums.BandFormat.Char, RasterPixelType.S8 },
        { Enums.BandFormat.Ushort, RasterPixelType.U16 },
        { Enums.BandFormat.Short, RasterPixelType.S16 },
        { Enums.BandFormat.Uint, RasterPixelType.U32 },
        { Enums.BandFormat.Int, RasterPixelType.S32 },
        { Enums.BandFormat.Float, RasterPixelType.F32 },
        { Enums.BandFormat.Double, RasterPixelType.F64 },
        { Enums.BandFormat.Complex, RasterPixelType.C64 },
        { Enums.BandFormat.Dpcomplex, RasterPixelType.C128 },
    };

    [Theory]
    [MemberData(nameof(BandFormats))]
    public void Band_formats_round_trip_through_the_core_vocabulary(Enums.BandFormat format, RasterPixelType pixelType)
    {
        Assert.Equal(pixelType, RasterBandFormats.ToCore(format));
        Assert.Equal(format, RasterBandFormats.ToVips(pixelType));
    }

    [Theory]
    [InlineData(Enums.BandFormat.Notset)]
    [InlineData((Enums.BandFormat)12)]
    public void An_unknown_band_format_maps_to_unknown(Enums.BandFormat format)
    {
        Assert.Equal(RasterPixelType.Unknown, RasterBandFormats.ToCore(format));
    }

    [Theory]
    [InlineData(RasterPixelType.Unknown)]
    [InlineData(RasterPixelType.U1)]
    [InlineData(RasterPixelType.U2)]
    [InlineData(RasterPixelType.U4)]
    public void An_unsupported_pixel_type_maps_to_no_band_format(RasterPixelType pixelType)
    {
        Assert.Equal(Enums.BandFormat.Notset, RasterBandFormats.ToVips(pixelType));
    }

    [Theory]
    [InlineData(1, RasterPixelType.U8, true)]
    [InlineData(1, RasterPixelType.F32, true)]
    [InlineData(1, RasterPixelType.S32, true)]
    [InlineData(3, RasterPixelType.U8, true)]
    [InlineData(4, RasterPixelType.U8, true)]
    [InlineData(3, RasterPixelType.F32, false)]
    [InlineData(4, RasterPixelType.S16, false)]
    [InlineData(1, RasterPixelType.C64, false)]
    [InlineData(1, RasterPixelType.C128, false)]
    [InlineData(1, RasterPixelType.U1, false)]
    [InlineData(1, RasterPixelType.Unknown, false)]
    public void IsConvertible_keeps_colour_rasters_eight_bit(int bandCount, RasterPixelType target, bool expected)
    {
        Assert.Equal(expected, RasterBandFormats.IsConvertible(bandCount, target));
    }
}
