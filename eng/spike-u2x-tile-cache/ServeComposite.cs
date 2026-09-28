using NetVips;
using Spatial.Contracts;

namespace Spatial.Spike.TileCache;

/// <summary>
/// The cost a per-layer tile cache would add to <b>every served tile</b>: the
/// step where the per-layer rasters a request needs are composited into the
/// one image the client receives.
///
/// Under the whole-map cache this step happens once, inside the renderer, and
/// its result is cached; under a per-layer cache it happens on every request
/// and nothing caches the composite, because any layer's next edit invalidates
/// it. So this number is the tax the per-layer design levies forever, against
/// a one-off saving — and it is the number that decides the bead.
///
/// It is an <b>emulation</b>, not a shipped path, and is labelled as such in the
/// output. It decodes each cached per-layer PNG and blends bottom-to-top with
/// libvips, which is the composition <c>Spatial.Imagery.Vips</c> already
/// performs for the imagery stack. A real design could cache raw buffers or
/// merge the layers as geometry before rasterising and would pay less; the
/// decode is kept in so the figure is the pessimistic one.
/// </summary>
internal static class ServeComposite
{
    /// <summary>Composites <paramref name="layers"/> (bottom to top) into one PNG, as a serve would.</summary>
    public static byte[] Composite(IReadOnlyList<byte[]> layers, int width, int height)
    {
        if (layers.Count == 0)
        {
            throw SpatialException.BadArguments("A composite needs at least one layer.");
        }

        // libvips is lazy, so every source image has to stay alive until the
        // encode has run; the scratch images are collected and released here
        // rather than at the end of each loop turn.
        var held = new List<Image>(layers.Count * 2);
        var streams = new List<MemoryStream>(layers.Count);        Image? accumulated = null;
        try
        {
            foreach (var content in layers)
            {
                var stream = new MemoryStream(content, writable: false);
                streams.Add(stream);
                var loaded = Image.NewFromStream(stream, "tile.png");
                var cast = loaded.Cast(Enums.BandFormat.Uchar);
                var rgba = cast.Bands == 3 ? cast.AddAlpha() : cast.Copy();
                held.Add(loaded);
                held.Add(cast);
                held.Add(rgba);
                if (accumulated is null)
                {
                    accumulated = rgba.Copy(interpretation: Enums.Interpretation.Srgb);
                    held.Add(accumulated);
                    continue;
                }

                // Over: the painter's order the renderer already uses, so the
                // composite is the same image the whole-map render produces.
                var over = rgba.Composite2(accumulated, Enums.BlendMode.Over);
                var next = over.Copy(interpretation: Enums.Interpretation.Srgb);
                held.Add(over);
                held.Add(next);
                accumulated.Dispose();
                accumulated = next;
            }

            if (accumulated is null)
            {
                throw SpatialException.BadArguments("A composite needs at least one layer.");
            }

            var resized = accumulated.Width == width && accumulated.Height == height
                ? accumulated
                : accumulated.ThumbnailImage(width, height, size: Enums.Size.Force);
            if (!ReferenceEquals(resized, accumulated))
            {
                held.Add(resized);
            }

            return resized.WriteToBuffer(".png");
        }
        finally
        {
            foreach (var image in held)
            {
                image.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
