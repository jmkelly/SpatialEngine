using NetVips;
using Spatial.Contracts;

namespace Spatial.Imagery.Vips.Imagery;

/// <summary>
/// Loads and normalises imagery: configured sources only (never a
/// caller-supplied URL), every image cast to 8-bit and tagged sRGB with an
/// alpha band so the compositing stack sees a uniform shape.
/// </summary>
internal static class ImageryLoader
{
    public static string Resolve(IReadOnlyDictionary<string, string> sources, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw SpatialException.BadArguments("An imagery source name is required.");
        }

        if (!sources.TryGetValue(source, out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw SpatialException.BadArguments($"Imagery source '{source}' is not configured.");
        }

        if (!File.Exists(path))
        {
            throw SpatialException.Missing($"Imagery source '{source}' points at a file that does not exist.");
        }

        return path;
    }

    public static Image LoadExact(string path, int width, int height)
    {
        using var source = Image.NewFromFile(path);
        using var thumbnail = source.ThumbnailImage(width, height, size: Enums.Size.Force);
        return Normalise(thumbnail);
    }

    public static Image Normalise(Image image)
    {
        using var cast = image.Cast(Enums.BandFormat.Uchar);
        if (cast.Bands == 3)
        {
            using var rgba = cast.AddAlpha();
            return rgba.Copy(interpretation: Enums.Interpretation.Srgb);
        }

        return cast.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    public static Image Solid(int width, int height, VipsColor color)
    {
        byte[] pixel = [color.Red, color.Green, color.Blue, color.Alpha];
        using var one = Image.NewFromMemory(pixel, 1, 1, 4, Enums.BandFormat.Uchar);
        return one.Embed(0, 0, width, height, Enums.Extend.Copy)
            .Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>Wraps a vector buffer as a straight (non-premultiplied) 4-band sRGB image.</summary>
    public static Image FromBuffer(RasterBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var bands = buffer.BytesPerPixel;
        using var image = Image.NewFromMemory(
            Tight(buffer), buffer.Width, buffer.Height, bands, Enums.BandFormat.Uchar);
        using var rgba = bands == 3 ? image.AddAlpha() : image.Copy();
        using var straight = buffer.Premultiplied ? rgba.Unpremultiply() : rgba.Copy();
        return straight.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static ReadOnlyMemory<byte> Tight(RasterBuffer buffer)
    {
        var rowBytes = buffer.Width * buffer.BytesPerPixel;
        if (buffer.Stride == rowBytes)
        {
            return buffer.Pixels;
        }

        var tight = new byte[rowBytes * buffer.Height];
        for (var row = 0; row < buffer.Height; row++)
        {
            buffer.Pixels.Slice(row * buffer.Stride, rowBytes)
                .CopyTo(tight.AsMemory(row * rowBytes, rowBytes));
        }

        return tight;
    }
}
