using NetVips;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Imagery;

/// <summary>
/// Encodes a composed image with libvips' lazy chain. The container suffix
/// carries the quality so the encode stays one pipelined request.
/// </summary>
internal static class VipsEncoder
{
    public static RasterImage Encode(Image image, RasterFormat format, int quality)
    {
        byte[] content;
        try
        {
            content = image.WriteToBuffer(Suffix(format, quality));
        }
        catch (VipsException exception)
        {
            throw SpatialException.BadArguments(
                $"The imagery pipeline could not encode '{format}': {exception.Message}");
        }

        return new RasterImage(content, MediaType(format), image.Width, image.Height, format);
    }

    private static string Suffix(RasterFormat format, int quality)
    {
        var clamped = Math.Clamp(quality, 1, 100);
        return format switch
        {
            RasterFormat.Png => ".png",
            RasterFormat.Jpeg => FormattableString.Invariant($".jpg[Q={clamped}]"),
            RasterFormat.Webp => FormattableString.Invariant($".webp[Q={clamped}]"),
            RasterFormat.Tiff => ".tif",
            _ => throw SpatialException.BadArguments($"Unsupported raster format '{format}'."),
        };
    }

    private static string MediaType(RasterFormat format) => format switch
    {
        RasterFormat.Png => "image/png",
        RasterFormat.Jpeg => "image/jpeg",
        RasterFormat.Webp => "image/webp",
        RasterFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };
}
