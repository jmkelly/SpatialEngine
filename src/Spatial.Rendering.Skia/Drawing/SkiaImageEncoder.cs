using SkiaSharp;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// Encodes a raw raster buffer with Skia when no imagery pipeline is
/// configured. The production encoding path is <c>IRasterOperations</c>
/// (libvips); this is the vector-only fallback and supports PNG and JPEG.
/// </summary>
internal static class SkiaImageEncoder
{
    public static RasterImage Encode(RasterBuffer buffer, RasterFormat format, int quality)
    {
        var info = new SKImageInfo(buffer.Width, buffer.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromPixelCopy(info, buffer.Pixels.Span)
            ?? throw new InvalidOperationException("Skia could not wrap the raster buffer as an image.");
        using var data = image.Encode(EncodedFormat(format), Math.Clamp(quality, 0, 100))
            ?? throw new InvalidOperationException(FormattableString.Invariant($"Skia could not encode the image as {format}."));
        return new RasterImage(data.ToArray(), MediaType(format), buffer.Width, buffer.Height, format);
    }

    /// <summary>Whether the vector-only path can encode <paramref name="format"/> without libvips.</summary>
    public static bool Supports(RasterFormat format) => format is RasterFormat.Png or RasterFormat.Jpeg;

    private static SKEncodedImageFormat EncodedFormat(RasterFormat format) => format switch
    {
        RasterFormat.Png => SKEncodedImageFormat.Png,
        RasterFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        RasterFormat.Webp => SKEncodedImageFormat.Webp,
        _ => throw SpatialException.BadArguments(
            $"Encoding '{format}' requires the imagery pipeline, which is not configured."),
    };

    private static string MediaType(RasterFormat format) => format switch
    {
        RasterFormat.Png => "image/png",
        RasterFormat.Jpeg => "image/jpeg",
        RasterFormat.Webp => "image/webp",
        _ => "application/octet-stream",
    };
}
