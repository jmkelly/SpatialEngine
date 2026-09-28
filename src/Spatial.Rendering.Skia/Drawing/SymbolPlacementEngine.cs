using SkiaSharp;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// One feature's decided placement: the candidate it took and the boxes it
/// occupies in the candidate's frame. A null component means that part of the
/// symbol was dropped by the collision test.
/// </summary>
internal sealed record PlacedSymbol(SymbolCandidate Candidate, SymbolBox? Icon, SymbolBox? Text)
{
    public bool DrawsAnything => Icon is not null || Text is not null;
}

/// <summary>The local-frame rectangle a symbol part occupies: top-left plus size, in the candidate's frame.</summary>
internal readonly record struct SymbolBox(float Left, float Top, float Width, float Height)
{
    /// <summary>The box's extent in canvas pixels, rotated about the candidate's anchor.</summary>
    public PixelBox Extent(SymbolCandidate candidate) => candidate.Box(Left, Top, Width, Height);
}

/// <summary>One symbol competing for space, in the order the priority sort gave it.</summary>
internal sealed record SymbolCandidateRequest(
    int Layer,
    int Slot,
    double SortKey,
    SymbolOptions Options,
    SymbolFeature Feature);

/// <summary>
/// The placement pass: a deterministic greedy first-fit over every symbol in
/// the scene, in priority order (ADR-0080). Priority is <c>symbol-sort-key</c>,
/// then the style's document order, then the scene builder's
/// identity/envelope-centre order within a layer — a total order, so the same
/// style and the same features always place the same labels, whatever order the
/// store returned them in.
/// </summary>
/// <remarks>
/// The pass is separate from drawing so that priority can span layers: a
/// high-priority label in a later layer still wins a collision against an
/// earlier one, while the pixels are composited in document order.
/// </remarks>
internal static class SymbolPlacementEngine
{
    /// <summary>
    /// Decides a placement for every feature of every symbol layer, indexed by
    /// layer position; the entry is null where a feature was not placed.
    /// </summary>
    public static IReadOnlyList<PlacedSymbol?[]?> Place(
        IReadOnlyList<SceneLayer> layers, ViewportProjection projection, FontSession fonts, SpriteRegistry sprites)
    {
        var placed = new PlacedSymbol?[]?[layers.Count];
        var requests = Priority(layers, placed);
        if (requests.Count == 0)
        {
            return placed;
        }

        var collision = new List<PixelBox>();
        foreach (var request in requests)
        {
            placed[request.Layer]![request.Slot] = Place(request, projection, fonts, sprites, collision);
        }

        return placed;
    }

    /// <summary>
    /// Every symbol in the scene in competition order, and the per-layer result
    /// slots they are decided into. The sort key is the primary key; document
    /// order and the within-layer order are the stable tie-breaks that keep it
    /// a total order.
    /// </summary>
    private static List<SymbolCandidateRequest> Priority(
        IReadOnlyList<SceneLayer> layers, PlacedSymbol?[]?[] placed)
    {
        var requests = new List<SymbolCandidateRequest>();
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            if (layers[layerIndex].Layer.Paint is not SymbolPaint paint)
            {
                continue;
            }

            placed[layerIndex] = new PlacedSymbol?[layers[layerIndex].Symbols.Count];
            for (var featureIndex = 0; featureIndex < layers[layerIndex].Symbols.Count; featureIndex++)
            {
                var options = layers[layerIndex].Symbols[featureIndex].Options ?? paint.Options;
                requests.Add(new SymbolCandidateRequest(
                    layerIndex,
                    featureIndex,
                    options.SortKey,
                    options,
                    layers[layerIndex].Symbols[featureIndex]));
            }
        }

        return
        [
            .. requests
                .OrderBy(request => request.SortKey)
                .ThenBy(request => request.Layer)
                .ThenBy(request => request.Slot),
        ];
    }

    /// <summary>
    /// Tries the feature's candidates in order and takes the first that places
    /// anything; within a candidate the icon and the text are placed
    /// independently, so an icon that fits still draws when its label does not.
    /// </summary>
    private static PlacedSymbol? Place(
        SymbolCandidateRequest request,
        ViewportProjection projection,
        FontSession fonts,
        SpriteRegistry sprites,
        List<PixelBox> collision)
    {
        var (options, feature) = (request.Options, request.Feature);
        var hasText = !string.IsNullOrEmpty(feature.Text) && !string.IsNullOrEmpty(options.TextField);
        var hasIcon = !string.IsNullOrEmpty(feature.Icon);
        if (!hasText && !hasIcon)
        {
            return null;
        }

        var bundled = fonts.Resolve(options.Fonts);
        using var font = BundledFont.CreateFont(bundled.Face, options.Size);
        var label = hasText
            ? ShapedLabel.Shape(feature.Text!, font, font.Metrics, bundled.Shaper, options)
            : null;
        SymbolBox? iconBox = hasIcon ? IconBox(sprites.Get(feature.Icon!), options) : null;
        SymbolBox? textBox = label is null ? null : LabelBox(label, options);

        foreach (var candidate in SymbolCandidates.Generate(feature.Geometry, options, projection))
        {
            SymbolBox? icon = null;
            if (iconBox is { } box
                && Accept(collision, box.Extent(candidate), options.Padding, options.AllowIconOverlap, options))
            {
                icon = box;
            }

            SymbolBox? text = null;
            if (textBox is { } shape
                && Accept(collision, shape.Extent(candidate), options.Padding, options.AllowTextOverlap, options))
            {
                text = shape;
            }

            if (icon is null && text is null)
            {
                continue;
            }

            return new PlacedSymbol(candidate, icon, text);
        }

        return null;
    }

    /// <summary>The label's box in the candidate's frame, from the anchor and offset.</summary>
    private static SymbolBox LabelBox(ShapedLabel label, SymbolOptions options)
    {
        var (left, top) = Align(
            (float)(options.OffsetX * options.Size),
            (float)(options.OffsetY * options.Size),
            label.Width,
            label.Height,
            options.Anchor);
        return new SymbolBox(left, top, label.Width, label.Height);
    }

    /// <summary>The icon's box in the candidate's frame; the icon is centred on the anchor.</summary>
    private static SymbolBox IconBox(SKPicture picture, SymbolOptions options)
    {
        var cull = picture.CullRect;
        var scale = (float)options.IconSize;
        return new SymbolBox(
            -(cull.Width * scale) / 2,
            -(cull.Height * scale) / 2,
            cull.Width * scale,
            cull.Height * scale);
    }

    /// <summary>
    /// Adds the padded box to the collision list when it is free, and reports
    /// whether it was placed. <c>symbol-allow-overlap</c> and the per-component
    /// flags bypass the test; <c>symbol-ignore-placement</c> bypasses it and
    /// leaves the list alone, so an ignored label neither blocks nor is blocked.
    /// </summary>
    private static bool Accept(
        List<PixelBox> collision, PixelBox box, double padding, bool allowOverlap, SymbolOptions options)
    {
        if (options.IgnorePlacement)
        {
            return true;
        }

        var padded = box.Inflate((float)padding);
        if (!allowOverlap && !options.AllowsOverlap && collision.Any(existing => existing.Intersects(padded)))
        {
            return false;
        }

        collision.Add(padded);
        return true;
    }

    private static (float Left, float Top) Align(float anchorX, float anchorY, float width, float height, SymbolAnchor anchor)
    {
        var left = anchor switch
        {
            SymbolAnchor.Left or SymbolAnchor.TopLeft or SymbolAnchor.BottomLeft => anchorX,
            SymbolAnchor.Right or SymbolAnchor.TopRight or SymbolAnchor.BottomRight => anchorX - width,
            _ => anchorX - width / 2,
        };
        var top = anchor switch
        {
            SymbolAnchor.Top or SymbolAnchor.TopLeft or SymbolAnchor.TopRight => anchorY,
            SymbolAnchor.Bottom or SymbolAnchor.BottomLeft or SymbolAnchor.BottomRight => anchorY - height,
            _ => anchorY - height / 2,
        };
        return (left, top);
    }
}
