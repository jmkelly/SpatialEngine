namespace RenderSpike;

using NetVips;

/// <summary>
/// The imagery + compositing stage, owned by NetVips/libvips. It takes the
/// vector rasterizer's premultiplied RGBA surface, blends it over an imagery
/// layer (a real raster basemap when one is supplied, a synthetic colour
/// field otherwise) and encodes the result. libvips is lazy and threaded, so
/// the whole chain — load, resize, composite, encode — is one pipelined
/// request, not a sequence of full-frame round trips.
/// </summary>
public static class ImageryCompositor
{
    public static byte[] Compose(byte[] overlayRgba, int width, int height, string? imageryPath = null)
    {
        // Both inputs must be tagged sRGB before composite2: libvips refuses
        // to colour-convert a bare "multiband" image to the compositing space.
        using var overlay = Image
            .NewFromMemory(overlayRgba, width, height, 4, Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);

        using var imagery = LoadImagery(width, height, imageryPath);
        using var composed = imagery.Composite(overlay, Enums.BlendMode.Over, premultiplied: true);

        return composed.WriteToBuffer(".png");
    }

    /// <summary>Loads a raster basemap, resized to the viewport; falls back to synthetic imagery.</summary>
    public static Image LoadImagery(int width, int height, string? imageryPath)
    {
        if (imageryPath is not null && File.Exists(imageryPath))
        {
            using var source = Image.NewFromFile(imageryPath);
            using var thumbnail = source.ThumbnailImage(width, height);
            return Normalise(thumbnail);
        }

        return SyntheticImagery(width, height);
    }

    /// <summary>A procedural colour field that behaves like imagery for the spike.</summary>
    public static Image SyntheticImagery(int width, int height)
    {
        // Image.Xyz is 2-band uint, not a colour image; build RGB from a
        // single-band field instead so composite2 sees four bands on both sides.
        using var field = Image.Sines(width, height, uchar: true);
        using var red = field.Linear([0.35], [28]).Cast(Enums.BandFormat.Uchar);
        using var green = field.Linear([0.5], [48]).Cast(Enums.BandFormat.Uchar);
        using var blue = field.Linear([0.65], [78]).Cast(Enums.BandFormat.Uchar);
        using var rgb = red.Bandjoin(green, blue);
        return Normalise(rgb);
    }

    private static Image Normalise(Image image)
    {
        using var cast = image.Cast(Enums.BandFormat.Uchar);
        if (cast.Bands == 3)
        {
            using var rgba = cast.AddAlpha();
            return rgba.Copy(interpretation: Enums.Interpretation.Srgb);
        }

        return cast.Copy(interpretation: Enums.Interpretation.Srgb);
    }
}
