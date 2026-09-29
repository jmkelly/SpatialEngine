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
/// it. So this number is the tax the per-layer design levies forever, against a
/// one-off saving — and it is the number that decides the bead.
///
/// It is an <b>emulation</b>, not a shipped path, and is labelled as such in the
/// output. The blend is bottom-to-top with libvips, which is the composition
/// <c>Spatial.Imagery.Vips</c> already performs for the imagery stack.
///
/// <b>Two arms, because this figure decides the bead and one number would not
/// be enough.</b> What the per-layer cache stores is a design choice, and the
/// two ends of it differ by an order of magnitude:
///
/// * <see cref="Composite"/> — the cache stores what arm A stores, a PNG.
///   Serving then pays a decode per layer per request. This is the
///   pessimistic end.
/// * <see cref="CompositePreDecoded"/> — the cache stores the decoded RGBA
///   buffer, which is what a per-layer tile cache would sensibly hold (it is
///   the MVT cache's situation one level down). Serving then pays only the
///   blend and the final encode. This is the optimistic end, and it is the
///   number a decision should be argued from, because a design that paid the
///   decode on every request would not be built.
///
/// The honest answer is the <i>worse</i> of the two, because that is what a
/// careless implementation costs, and the better of the two, because that is
/// what a careful one costs. If the decision does not come out the same either
/// way, the emulation is too crude to decide anything and that is the finding.
/// </summary>
internal static class ServeComposite
{
    /// <summary>
    /// Composites encoded per-layer tiles (bottom to top) into one PNG,
    /// decoding each — the pessimistic arm.
    /// </summary>
    public static byte[] Composite(IReadOnlyList<byte[]> layers, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0)
        {
            throw SpatialException.BadArguments("A composite needs at least one layer.");
        }

        var held = new List<Image>((layers.Count * 4) + 1);
        try
        {
            var decoded = new List<Image>(layers.Count);
            foreach (var content in layers)
            {
                var loaded = Image.NewFromBuffer(content);
                var cast = loaded.Cast(Enums.BandFormat.Uchar);
                var rgba = cast.Bands == 3 ? cast.AddAlpha() : cast.Copy();
                held.Add(loaded);
                held.Add(cast);
                held.Add(rgba);
                decoded.Add(rgba);
            }

            return Blend(decoded, width, height, held);
        }
        finally
        {
            foreach (var image in held)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// Composites already-decoded per-layer rasters (bottom to top) into one
    /// PNG, paying only the blend and the encode — the optimistic arm. The
    /// caller owns the images and keeps them alive for the duration.
    /// </summary>
    public static byte[] CompositePreDecoded(IReadOnlyList<Image> layers, int width, int height) =>
        Blend(layers, width, height, []);

    /// <summary>
    /// The bottom-to-top "over" blend and the final encode, shared by both
    /// arms. <paramref name="scratch"/> collects the intermediate images, which
    /// libvips needs alive until the encode has run, so the caller disposes
    /// them.
    /// </summary>
    private static byte[] Blend(IReadOnlyList<Image> layers, int width, int height, List<Image> scratch)
    {
        if (layers.Count == 0)
        {
            throw SpatialException.BadArguments("A composite needs at least one layer.");
        }

        // libvips is lazy, so every intermediate has to stay alive until the
        // encode has run; the scratch images are released by the caller's
        // finally rather than at the end of each loop turn.
        Image? accumulated = null;
        foreach (var layer in layers)
        {
            if (accumulated is null)
            {
                accumulated = layer.Copy(interpretation: Enums.Interpretation.Srgb);
                scratch.Add(accumulated);
                continue;
            }

            // Over: the painter's order the renderer already uses, so the
            // composite is the same image the whole-map render produces.
            var over = layer.Composite2(accumulated, Enums.BlendMode.Over);
            var next = over.Copy(interpretation: Enums.Interpretation.Srgb);
            scratch.Add(over);
            scratch.Add(next);
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
            scratch.Add(resized);
        }

        return resized.WriteToBuffer(".png");
    }
}
