using NetVips;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Imagery;

/// <summary>Owns every lifted image in a composite stack so the caller can encode then release.</summary>
internal sealed class Composition : IDisposable
{
    private readonly List<Image> _images;

    internal Composition(List<Image> images, Image result)
    {
        _images = images;
        Result = result;
    }

    public Image Result { get; }

    public void Dispose()
    {
        foreach (var image in _images)
        {
            image.Dispose();
        }
    }
}

/// <summary>
/// Builds the bottom-to-top raster stack as one lazy libvips chain: an
/// optional solid background, configured imagery resized to the frame, and
/// raw buffers from the vector rasterizer.
/// </summary>
internal static class VipsCompositor
{
    public static Composition Compose(
        RasterViewport viewport,
        IReadOnlyList<RasterLayer> layers,
        IReadOnlyDictionary<string, string> sources,
        string? background,
        bool transparent)
    {
        Validate(viewport, layers, background, transparent);
        var owned = new List<Image>(layers.Count + 2);
        Image result;
        var index = 0;
        if (background is not null || !transparent)
        {
            result = ImageryLoader.Solid(viewport.Width, viewport.Height, VipsColorParser.Parse(background ?? "#ffffff"));
            owned.Add(result);
        }
        else
        {
            result = ToImage(layers[0], viewport, sources, owned);
            index = 1;
        }

        for (; index < layers.Count; index++)
        {
            var image = ToImage(layers[index], viewport, sources, owned);
            var blended = result.Composite(image, RasterBlending.ToVips(Blend(layers[index])), premultiplied: false);
            owned.Add(blended);
            result = blended;
        }

        return new Composition(owned, result);
    }

    private static void Validate(RasterViewport viewport, IReadOnlyList<RasterLayer> layers, string? background, bool transparent)
    {
        if (!viewport.IsValid)
        {
            throw SpatialException.BadArguments("The viewport requires a non-empty bounds and a positive pixel size.");
        }

        if (layers.Count == 0 && background is null && transparent)
        {
            throw SpatialException.BadArguments("At least one layer is required.");
        }
    }

    private static Image ToImage(
        RasterLayer layer, RasterViewport viewport, IReadOnlyDictionary<string, string> sources, List<Image> owned)
    {
        var image = layer switch
        {
            RasterBufferLayer buffer => FromBuffer(buffer, viewport),
            RasterSourceLayer source => FromSource(source, viewport, sources),
            _ => throw SpatialException.BadArguments("Unsupported raster layer."),
        };
        owned.Add(image);
        return image;
    }

    private static Image FromBuffer(RasterBufferLayer layer, RasterViewport viewport)
    {
        if (layer.Buffer.Width != viewport.Width || layer.Buffer.Height != viewport.Height)
        {
            throw SpatialException.BadArguments("A buffer layer must match the viewport pixel size.");
        }

        return ImageryLoader.FromBuffer(layer.Buffer);
    }

    private static Image FromSource(RasterSourceLayer layer, RasterViewport viewport, IReadOnlyDictionary<string, string> sources)
    {
        var path = ImageryLoader.Resolve(sources, layer.Source);
        var loaded = ImageryLoader.LoadExact(path, viewport.Width, viewport.Height);
        var blended = RasterBlending.ApplyOpacity(loaded, layer.Opacity);
        loaded.Dispose();
        return blended;
    }

    private static RasterBlend Blend(RasterLayer layer) => layer switch
    {
        RasterBufferLayer buffer => buffer.Blend,
        RasterSourceLayer source => source.Blend,
        _ => RasterBlend.Over,
    };
}
