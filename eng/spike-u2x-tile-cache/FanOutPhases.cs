using System.Diagnostics;
using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips;
using Spatial.Operations.NetTopologySuite;
using Spatial.Rendering.Skia;
using Spatial.Stores.Memory;
using Spatial.Tiling.WebMercator;
using Spatial.Transformations.ProjNet;


namespace Spatial.Spike.TileCache;
/// <summary>Invalidation measurements: what a single-layer data edit or style save throws out.</summary>
internal static class FanOutPhases
{
/// <summary>A single-layer data write: how many warm entries does it throw out, and how many really changed?</summary>
internal static async Task<FanOut> FanOutAsync(
    Options options,
    string @case,
    LayerSpec layer,
    IReadOnlyList<LayerSpec> published,
    IReadOnlyList<MapLayerSource> sources,
    CountingCache cache,
    IMapRenderer renderer,
    ITileScheme scheme,
    IReadOnlyList<TileCoordinate> tiles,
    string style,
    string versionBefore,
    SampleReport perLayerRender,
    CancellationToken cancellationToken)
{
    // Warm the whole map again, keeping the bytes, so "did the pixels
    // actually change" is a byte comparison and not an inference. The
    // cache is cleared first so each case starts from the same warm working
    // set and the `warm` column means one map, not every case so far.
    await cache.ClearAsync(cancellationToken);
    var before = new Dictionary<TileCoordinate, byte[]>();
    foreach (var tile in tiles)
    {
        var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionBefore, Program.Request(sources, style), cancellationToken);
        before[tile] = result.Image.Content;
    }

    var warmEntries = cache.Entries;

    // The edit: one feature added to one layer's dataset, through the
    // store's own write face, which is what moves its content version.
    var store = (MemoryStore)sources[0].Features;
    var schema = Basemap.Schema(layer.Family);
    // The added feature has to carry a geometry of the layer's own family,
    // or the renderer draws nothing for it and the edit is a no-op that
    // measures the cache's behaviour on an empty change rather than on a
    // real one. Basemap already knows how to draw each family; reuse it.
    var editRandom = new Random(7);
    var point = GeometryFactory.CreatePoint(13.404, 52.520, CoordinateReference.Epsg(4326));
    var edited = Basemap.GeometryFor(layer.Family, point, editRandom);
    var feature = new Feature(
        FeatureId.Unassigned,
        schema,
        layer.Family == GeometryFamily.Point
            ? [AttributeValue.FromString("spike-edit"), AttributeValue.FromString("place"), AttributeValue.FromGeometry(edited)]
            : [AttributeValue.FromString("spike-edit"), AttributeValue.FromGeometry(edited)]);
    await store.WriteAsync(layer.Dataset, new FeatureBatch(schema, [feature]), null, cancellationToken);

    // The style did not change, so the new version differs only in the fold.
    var versionAfter = await Program.VersionAsyncFromSources(sources, published, style, cancellationToken);

    var invalidated = 0;
    var changed = 0;
    foreach (var tile in tiles)
    {
        var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionAfter, Program.Request(sources, style), cancellationToken);
        if (!result.Cached)
        {
            invalidated++;
            if (!before[tile].AsSpan().SequenceEqual(result.Image.Content))
            {
                changed++;
            }
        }
    }

    return new FanOut(@case, layer.Dataset, warmEntries, invalidated, changed, perLayerRender.P50Milliseconds, published.Count);
}

/// <summary>A single-layer style save: the same question, with no data write at all.</summary>
internal static async Task<FanOut> StyleFanOutAsync(
    Options options,
    LayerSpec layer,
    IReadOnlyList<LayerSpec> published,
    IReadOnlyList<MapLayerSource> sources,
    CountingCache cache,
    IMapRenderer renderer,
    ITileScheme scheme,
    IReadOnlyList<TileCoordinate> tiles,
    string style,
    string versionBefore,
    SampleReport perLayerRender,
    CancellationToken cancellationToken)
{
    // Warm the whole map again, clearing first so each case starts from the
    // same warm working set (see FanOutAsync).
    await cache.ClearAsync(cancellationToken);
    var before = new Dictionary<TileCoordinate, byte[]>();
    foreach (var tile in tiles)
    {
        var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionBefore, Program.Request(sources, style), cancellationToken);
        before[tile] = result.Image.Content;
    }

    var warmEntries = cache.Entries;

    // Recompose the publication's style with exactly one layer restyled,
    // in the published order, which is what a style save writes.
    var restyled = Basemap.Restyle(layer);
    var after = Program.RestyledStyle(published, layer, restyled);
    var versionAfter = await Program.VersionAsyncFromSources(sources, published, after, cancellationToken);

    var invalidated = 0;
    var changed = 0;
    foreach (var tile in tiles)
    {
        var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionAfter, Program.Request(sources, after), cancellationToken);
        if (!result.Cached)
        {
            invalidated++;
            if (!before[tile].AsSpan().SequenceEqual(result.Image.Content))
            {
                changed++;
            }
        }
    }

    return new FanOut("style", layer.Dataset, warmEntries, invalidated, changed, perLayerRender.P50Milliseconds, published.Count);
}
}
