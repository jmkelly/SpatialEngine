using NetVips;
using Spatial.Contracts;

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

    private static readonly Dictionary<RasterFormat, string> PlainSuffixes = new()
    {
        [RasterFormat.Png] = ".png",
        [RasterFormat.Tiff] = ".tif",
    };

    private static readonly Dictionary<RasterFormat, string> QualitySuffixes = new()
    {
        [RasterFormat.Jpeg] = ".jpg",
        [RasterFormat.Webp] = ".webp",
    };

    private static readonly Dictionary<RasterFormat, string> MediaTypes = new()
    {
        [RasterFormat.Png] = "image/png",
        [RasterFormat.Jpeg] = "image/jpeg",
        [RasterFormat.Webp] = "image/webp",
        [RasterFormat.Tiff] = "image/tiff",
    };

    private static string Suffix(RasterFormat format, int quality)
    {
        if (PlainSuffixes.TryGetValue(format, out var plain))
        {
            return plain;
        }

        if (QualitySuffixes.TryGetValue(format, out var qualitySuffix))
        {
            var clamped = Math.Clamp(quality, 1, 100);
            return FormattableString.Invariant($"{qualitySuffix}[Q={clamped}]");
        }

        throw SpatialException.BadArguments($"Unsupported raster format '{format}'.");
    }

    private static string MediaType(RasterFormat format) =>
        MediaTypes.TryGetValue(format, out var mediaType) ? mediaType : "application/octet-stream";
}
