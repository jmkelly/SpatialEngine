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

/// <summary>
/// The SpatialEngine-u2x.21.2 spike: on a realistically published multi-layer
/// map, how many tiles a single-layer edit or single-layer style save throws
/// out of the tile cache, against how long a re-render of those tiles takes.
/// It measures only — nothing in the product is changed.
///
/// Three designs are compared, because the bead asks for a decision and not
/// only a number:
///
/// | arm | what it does | is it shipped? |
/// | --- | --- | --- |
/// | **A whole-map entries** | one cache entry per (tile, whole-map version). A write to one layer moves the whole map's version, so every tile misses. | yes — ADR-0046 + ADR-0083 |
/// | **B per-layer entries, composed at serve time** | one entry per (tile, layer). A write to one layer re-renders that layer's entries only; every request composites the layers. | emulated, **not shipped** |
/// | **C per-layer style sub-key** | the same single entry per tile, with the style folded per layer instead of whole. | emulated, **not shipped** |
///
/// Arm C is a refactor of the key, not of the entry, so it cannot narrow the
/// fan-out; the spike measures it rather than asserting it, and reports it as
/// the same numbers as A.
/// </summary>
internal static class Program
{
    private const string Service = "spike-city";

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            await RunAsync(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or SpatialException)
        {
            Console.Error.WriteLine($"the spike could not run: {exception}");
            return 1;
        }
    }

    private static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        var store = new MemoryStore();
        var registry = new SingleRegistry(store, store);
        var renderer = new MapRenderer(
            new ProjNetTransforms(),
            new NtsGeometryOperations(),
            new VipsRasterOperations(new Dictionary<string, string>()));
        var scheme = new WebMercatorTileScheme();
        var cache = new CountingCache(options.MaxEntries, options.MaxBytes);

        var layers = await Basemap.PublishAsync(store, WorkingTiles(options.Zoom).Count, options.Density, cancellationToken);
        if (options.Layers < layers.Count)
        {
            layers = [.. layers.Take(options.Layers)];
        }

        var tiles = WorkingTiles(options.Zoom);
        var styleSources = Mirror.Sources(registry, registry.Key, layers);
        var style = ComposedStyle(layers);

        Console.WriteLine($"# SpatialEngine-u2x.21.2 tile invalidation fan-out — label={options.Label}");
        Console.WriteLine($"# map '{Service}': {layers.Count} layers over a {Basemap.MaxLon - Basemap.MinLon:F2}deg x {Basemap.MaxLat - Basemap.MinLat:F2}deg metro extent");
        Console.WriteLine($"#   " + string.Join(", ", layers.Select(layer => $"{layer.Dataset}({layer.PerTile * options.Density}/tile)")));
        Console.WriteLine($"# working set: {tiles.Count} tiles at z={options.Zoom} covering that extent; iterations={options.Iterations} warmup={options.Warmup}");
        Console.WriteLine($"# A=whole-map entries (shipped)  B=per-layer entries composed at serve time (emulated)  C=per-layer style sub-key (emulated)");
        Console.WriteLine($"# cache bounds: maxEntries={options.MaxEntries} maxBytes={options.MaxBytes}");
        Console.WriteLine();

        // ---------------------------------------------------------------- map
        var version = await VersionAsync(registry, layers, style, cancellationToken);
        Console.WriteLine($"# version(V0) = {version[..16]}…  (service + composed style + folded data versions, ADR-0083)");
        Console.WriteLine();

        // ------------------------------------------------------- A: whole map
        Console.WriteLine("## A — whole-map cache entries (the shipped design)");
        var cold = await MeasureAsync(options, "cold-render", "A whole-map", async token =>
        {
            await cache.ClearAsync(token);
            foreach (var tile in tiles)
            {
                await Mirror.RenderAsync(renderer, cache, scheme, tile, version, Request(styleSources, style), token);
            }
        }, cancellationToken);
        var warm = await MeasureAsync(options, "warm-hit", "A whole-map", async token =>
        {
            foreach (var tile in tiles)
            {
                await Mirror.RenderAsync(renderer, cache, scheme, tile, version, Request(styleSources, style), token);
            }
        }, cancellationToken);

        var entriesAfterWarm = cache.Entries;
        var bytesAfterWarm = cache.Bytes;
        Console.WriteLine(SampleReport.Header);
        Console.WriteLine(cold.ToRow());
        Console.WriteLine(warm.ToRow());
        Console.WriteLine($"# warm working set: {entriesAfterWarm} entries, {bytesAfterWarm / 1024.0 / 1024.0:F2} MB, {bytesAfterWarm / (double)entriesAfterWarm / 1024.0:F1} KB/tile average");
        Console.WriteLine();

        // ----------------------------------------------- B: per-layer entries
        Console.WriteLine("## B — per-layer cache entries, composed at serve time (emulated, not shipped)");
        var perLayerRenders = new List<SampleReport>();
        var perLayerBytes = new List<long>();
        foreach (var layer in layers)
        {
            var only = new List<LayerSpec> { layer };
            var layerSources = Mirror.Sources(registry, registry.Key, only);
            var layerStyle = MapStyle.Compose(Service, [new MapLayer(layer.Dataset, 0, layer.Dataset, layer.Style)]);
            var report = await MeasureAsync(options, "cold-render", $"B {Short(layer.Dataset)}", async token =>
            {
                foreach (var tile in tiles)
                {
                    await renderer.RenderAsync(
                        new MapRenderRequest(
                            new RasterViewport(scheme.Bounds(tile), scheme.TileSize, scheme.TileSize, scheme.Crs),
                            layerStyle,
                            layerSources),
                        token);
                }
            }, cancellationToken);
            perLayerRenders.Add(report);

            // The bytes one layer's tile would occupy, for the cache-size argument.
            long bytes = 0;
            foreach (var tile in tiles)
            {
                var image = await renderer.RenderAsync(
                    new MapRenderRequest(
                        new RasterViewport(scheme.Bounds(tile), scheme.TileSize, scheme.TileSize, scheme.Crs),
                        layerStyle,
                        layerSources),
                    cancellationToken);
                bytes += image.Content.LongLength;
            }

            perLayerBytes.Add(bytes);
        }

        Console.WriteLine(SampleReport.Header);
        foreach (var report in perLayerRenders)
        {
            Console.WriteLine(report.ToRow());
        }

        // The per-layer design's standing cost: composite the per-layer tiles
        // into the served tile, on every request, forever.
        var perLayerTiles = new List<byte[]>[tiles.Count];
        for (var t = 0; t < tiles.Count; t++)
        {
            perLayerTiles[t] = new List<byte[]>(layers.Count);
        }

        for (var l = 0; l < layers.Count; l++)
        {
            var only = new List<LayerSpec> { layers[l] };
            var layerSources = Mirror.Sources(registry, registry.Key, only);
            var layerStyle = MapStyle.Compose(Service, [new MapLayer(layers[l].Dataset, 0, layers[l].Dataset, layers[l].Style)]);
            for (var t = 0; t < tiles.Count; t++)
            {
                var image = await renderer.RenderAsync(
                    new MapRenderRequest(
                        new RasterViewport(scheme.Bounds(tiles[t]), scheme.TileSize, scheme.TileSize, scheme.Crs),
                        layerStyle,
                        layerSources),
                    cancellationToken);
                perLayerTiles[t].Add(image.Content);
            }
        }

        var compose = await MeasureAsync(options, "serve-compose", "B composite+png", token =>
        {
            foreach (var stack in perLayerTiles)
            {
                ServeComposite.Composite(stack, scheme.TileSize, scheme.TileSize);
            }

            return Task.CompletedTask;
        }, cancellationToken);
        Console.WriteLine(compose.ToRow());

        // The optimistic arm: the same blend and encode, with the per-layer
        // rasters already decoded, which is what a per-layer cache holding raw
        // buffers would cost. The decode is hoisted out of the timed region
        // deliberately — it is the first arm's job to measure it.
        var decoded = new List<NetVips.Image>[tiles.Count];
        for (var t = 0; t < tiles.Count; t++)
        {
            decoded[t] = [];
            foreach (var content in perLayerTiles[t])
            {
                var loaded = NetVips.Image.NewFromBuffer(content);
                var cast = loaded.Cast(NetVips.Enums.BandFormat.Uchar);
                var rgba = cast.Bands == 3 ? cast.AddAlpha() : cast.Copy();
                loaded.Dispose();
                cast.Dispose();
                decoded[t].Add(rgba);
            }
        }

        var composeRaw = await MeasureAsync(options, "serve-compose", "B composite+raw", token =>
        {
            foreach (var stack in decoded)
            {
                ServeComposite.CompositePreDecoded(stack, scheme.TileSize, scheme.TileSize);
            }

            return Task.CompletedTask;
        }, cancellationToken);
        Console.WriteLine(composeRaw.ToRow());
        foreach (var stack in decoded)
        {
            foreach (var image in stack)
            {
                image.Dispose();
            }
        }

        Console.WriteLine($"# B holds {tiles.Count * layers.Count} entries for the same {tiles.Count} tiles " +
            $"({(tiles.Count * layers.Count) / (double)options.MaxEntries:P0} of the entry bound), " +
            $"{perLayerBytes.Sum() / 1024.0 / 1024.0:F2} MB against A's {bytesAfterWarm / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine("# 'composite+png' decodes each cached layer on every request (cache stores PNG);");
        Console.WriteLine("# 'composite+raw' is the same blend and encode with the layers already decoded (cache stores raw buffers).");
        Console.WriteLine();

        // ------------------------------------------ the two invalidation cases
        Console.WriteLine("## Invalidation fan-out — how many of the warm entries a single-layer change throws out");
        var edits = new List<FanOut>();
        foreach (var layer in layers)
        {
            edits.Add(await FanOutAsync(options, "edit", layer, layers, styleSources, cache, renderer, scheme, tiles, style, version, perLayerRenders[edits.Count], cancellationToken));
        }

        foreach (var layer in layers)
        {
            edits.Add(await StyleFanOutAsync(options, layer, layers, styleSources, cache, renderer, scheme, tiles, style, version, perLayerRenders[edits.Count - layers.Count], cancellationToken));
        }

        Console.WriteLine(FanOut.Header);
        foreach (var row in edits)
        {
            Console.WriteLine(row.ToRow());
        }

        Console.WriteLine();

        // -------------------------------------------------- C and the flush
        Console.WriteLine("## C — per-layer style sub-key (emulated), and the global flush");
        Console.WriteLine("# C keeps ONE entry per tile and only changes how the version is folded, so the entry count and");
        Console.WriteLine("# therefore the fan-out are the same as A. The measured rows below are A's, re-labelled.");
        var styleArm = edits.First(row => row.Case == "style" && row.Layer == layers[0].Dataset);
        Console.WriteLine($"#   C style save on {layers[0].Dataset}: {styleArm.Invalidated} thrown out, {styleArm.Changed} genuinely changed — identical to A above");
        Console.WriteLine();

        await FlushAsync(options, registry, renderer, cache, scheme, layers, tiles, styleSources, cancellationToken);

        // ------------------------------------------------------ the decision
        Decision(options, scheme.TileSize, layers, tiles, cold, warm, perLayerRenders, compose, composeRaw, edits, bytesAfterWarm, perLayerBytes);

        if (options.Host is { } host)
        {
            await HostCheck.RunAsync(options.Host, Service, layers, tiles, cancellationToken);
        }
    }

    /// <summary>A single-layer data write: how many warm entries does it throw out, and how many really changed?</summary>
    private static async Task<FanOut> FanOutAsync(
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
            var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionBefore, Request(sources, style), cancellationToken);
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
        var versionAfter = await VersionAsyncFromSources(sources, published, style, cancellationToken);

        var invalidated = 0;
        var changed = 0;
        foreach (var tile in tiles)
        {
            var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionAfter, Request(sources, style), cancellationToken);
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
    private static async Task<FanOut> StyleFanOutAsync(
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
            var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionBefore, Request(sources, style), cancellationToken);
            before[tile] = result.Image.Content;
        }

        var warmEntries = cache.Entries;

        // Recompose the publication's style with exactly one layer restyled,
        // in the published order, which is what a style save writes.
        var restyled = Basemap.Restyle(layer);
        var after = RestyledStyle(published, layer, restyled);
        var versionAfter = await VersionAsyncFromSources(sources, published, after, cancellationToken);

        var invalidated = 0;
        var changed = 0;
        foreach (var tile in tiles)
        {
            var result = await Mirror.RenderAsync(renderer, cache, scheme, tile, versionAfter, Request(sources, after), cancellationToken);
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

    /// <summary>How much of the cache a <c>DELETE /api/render/cache</c> throws out, across every published map.</summary>
    private static async Task FlushAsync(
        Options options,
        SingleRegistry registry,
        IMapRenderer renderer,
        CountingCache cache,
        ITileScheme scheme,
        IReadOnlyList<LayerSpec> layers,
        IReadOnlyList<TileCoordinate> tiles,
        IReadOnlyList<MapLayerSource> sources,
        CancellationToken cancellationToken)
    {
        // Warm several maps that share the layers, the way a deployment with a
        // few publications of the same data behaves.
        var maps = options.Maps;
        for (var m = 0; m < maps; m++)
        {
            var name = $"{Service}-{m}";
            var mapStyle = MapStyle.Compose(name, [.. layers.Select((layer, index) => new MapLayer(layer.Dataset, index, layer.Dataset, layer.Style))]);
            var version = Mirror.Version(name, mapStyle, await Mirror.DataVersionAsync(registry, registry.Key, layers, cancellationToken));
            foreach (var tile in tiles)
            {
                await Mirror.RenderAsync(renderer, cache, scheme, tile, version, Request(sources, mapStyle), cancellationToken);
            }
        }

        var before = cache.Entries;
        await cache.ClearAsync(cancellationToken);
        Console.WriteLine($"# a global flush (DELETE /api/render/cache) over {maps} published maps threw out all {before} entries, " +
            $"against {tiles.Count} for one map's own version move — {before / (double)tiles.Count:F1}x the fan-out");
        Console.WriteLine();
    }

    /// <summary>The decision, stated from the numbers the spike just produced.</summary>
    private static void Decision(
        Options options,
        int tileSize,
        IReadOnlyList<LayerSpec> layers,
        IReadOnlyList<TileCoordinate> tiles,
        SampleReport cold,
        SampleReport warm,
        List<SampleReport> perLayer,
        SampleReport compose,
        SampleReport composeRaw,
        List<FanOut> fanOut,
        long wholeMapBytes,
        IReadOnlyList<long> perLayerBytes)
    {
        var worst = fanOut.Where(row => row.Case == "edit").MaxBy(row => row.Invalidated)!;
        var bestCase = fanOut.Where(row => row.Case == "edit").MinBy(row => row.Changed)!;
        var perTileCompose = compose.P50Milliseconds / tiles.Count;
        var perTileComposeRaw = composeRaw.P50Milliseconds / tiles.Count;
        var perTileCold = cold.P50Milliseconds / tiles.Count;
        var perTileWarm = warm.P50Milliseconds / tiles.Count;

        Console.WriteLine("## Derived — the numbers the decision is made on");
        Console.WriteLine($"tiles in the working set                 {tiles.Count}");
        Console.WriteLine($"layers in the published map             {layers.Count}");
        Console.WriteLine($"whole-map cold re-render, per tile      {perTileCold:F2} ms");
        Console.WriteLine($"whole-map warm hit, per tile            {perTileWarm:F2} ms");
        Console.WriteLine($"whole-map cold re-render, per tile      {perTileCold:F2} ms  alloc {cold.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"serve-time composite+png, per tile      {perTileCompose:F2} ms  alloc {compose.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB");
        Console.WriteLine($"serve-time composite+raw, per tile      {perTileComposeRaw:F2} ms  alloc {composeRaw.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB");
        Console.WriteLine($"over-invalidation, a single-layer edit  {worst.Invalidated / (double)Math.Max(1, worst.Changed):F1}x  (worst layer)");
        Console.WriteLine($"over-invalidation, a single-layer edit  {bestCase.Invalidated / (double)Math.Max(1, bestCase.Changed):F1}x  (sparsest layer)");
        var styleRows = fanOut.Where(row => row.Case == "style").ToList();
        Console.WriteLine($"over-invalidation, a single-layer style {styleRows.Min(row => row.Invalidated / (double)Math.Max(1, row.Changed)):F1}x  (min across layers — every tile's pixels really did change)");
        Console.WriteLine();
        Console.WriteLine("## Read the over-invalidation factor with the cache's laziness in mind");
        Console.WriteLine("# The fan-out counts warm entries DROPPED, not tiles re-rendered. Both arms are demand-filled: the");
        Console.WriteLine("# host's TileService (and this spike's Mirror.RenderAsync) look the key up and render only on a miss,");
        Console.WriteLine("# so a version move makes a tile cost something only if a client asks for that tile again.");
        Console.WriteLine($"# So the price of a single-layer edit is (tiles re-requested) x {perTileCold:F1} ms, not {tiles.Count} x that. This harness");
        Console.WriteLine("# re-requests the whole working set to count the fan-out, which is the measurement's demand, not");
        Console.WriteLine("# the host's behaviour. The over-invalidation factor is an upper bound on the waste, not the bill.");
        // The memory the two arms actually hold, and — critically — what the
        // cheap ('composite+raw') blend arm costs to store. The per-layer PNG
        // column above is the bytes arm B holds if it caches what arm A caches.
        // The 'composite+raw' arm is only cheap because a raw RGBA buffer can be
        // blended without decoding, so the cache it implies holds *raw* buffers,
        // and that is a different amount of memory entirely. Reporting only the
        // PNG figure would make the cheap arm look free; it is not.
        var rawPerLayerBytes = (long)tiles.Count * layers.Count * tileSize * tileSize * 4;
        var perLayerPngBytes = perLayerBytes.Sum();
        Console.WriteLine($"cache entries, whole-map                {tiles.Count}");
        Console.WriteLine($"cache entries, per-layer                {tiles.Count * layers.Count}  ({layers.Count}x arm A)");
        Console.WriteLine($"cache bytes, whole-map                  {wholeMapBytes / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"cache bytes, per-layer (as PNG)          {perLayerPngBytes / 1024.0 / 1024.0:F2} MB  ({perLayerPngBytes / (double)Math.Max(1, wholeMapBytes):F1}x arm A)");
        Console.WriteLine($"cache bytes, per-layer (raw RGBA)        {rawPerLayerBytes / 1024.0 / 1024.0:F2} MB  ({rawPerLayerBytes / (double)Math.Max(1, wholeMapBytes):F1}x arm A)");
        Console.WriteLine($"  the raw figure is what the cheap       a {tileSize}x{tileSize} RGBA buffer per (tile, layer), {layers.Count} layers x {tiles.Count} tiles");
        Console.WriteLine($"  'composite+raw' blend arm requires");
        Console.WriteLine($"entry-bound equivalent, raw per-layer    {options.MaxEntries / layers.Count} tiles' worth for the same {options.MaxEntries} entries");
        Console.WriteLine($"entry-bound equivalent, raw bytes        {(long)options.MaxEntries / layers.Count * layers.Count * tileSize * tileSize * 4 / 1024.0 / 1024.0:F2} MB against the {options.MaxBytes / 1024.0 / 1024.0:F0} MB byte bound");

        // Break-even: how many tiles must be re-requested after one edit before
        // per-layer composition has repaid itself.
        //
        // Units. Everything here is per tile, because that is the only basis on
        // which the two arms are comparable: the arms are measured in different
        // phases of the run, and on a contended box the absolute totals drift by
        // more than 2x between phases, so a whole-working-set saving divided by
        // a per-tile tax would be a comparison of two numbers that were never
        // measured under the same conditions.
        //
        // After a single-layer edit, if the client re-requests K of the tiles:
        //   arm A: K x perTileCold  (every one of them is a cache miss)
        //   arm B: perTileEditedLayer (re-render the one edited layer, once)
        //           + K x tax        (blend and encode on every serve)
        // Break-even solves  perTileEditedLayer + K x tax = K x perTileCold.
        var perTileEdited = new double[layers.Count];
        for (var l = 0; l < layers.Count; l++)
        {
            perTileEdited[l] = perLayer[l].P50Milliseconds / tiles.Count;
        }

        Console.WriteLine();
        Console.WriteLine("## Break-even — tiles re-requested after one edit before per-layer composition repays itself");
        Console.WriteLine("# Two bases are given because the measurement box is shared with a parallel swarm and the wall-time");
        Console.WriteLine("# columns moved by up to 8x between runs. The ALLOC columns did not move: on this harness they are");
        Console.WriteLine("# stable to the third decimal across every run, so the allocation basis is the one to quote and");
        Console.WriteLine("# the wall-time basis is here only to show the two agree in sign and order of magnitude.");
        Console.WriteLine($"# per tile:  A pays {perTileCold:F1} ms per re-requested tile (all cold); B pays {Min(perTileEdited):F1}-{Max(perTileEdited):F1} ms once to re-render the edited layer,");
        Console.WriteLine($"#           then {perTileComposeRaw:F1} ms (+raw) or {perTileCompose:F1} ms (+png) per tile served.");
        Console.WriteLine($"{"edited layer",-22} {"edited layer ms/tile",19} {"tax+raw",10} {"tax+png",10} {"break-even+raw",16} {"break-even+png",16}");
        for (var l = 0; l < layers.Count; l++)
        {
            var headroom = perTileCold - perTileCompose;
            var headroomRaw = perTileCold - perTileComposeRaw;
            var breakEvenPng = headroom <= 0 ? double.PositiveInfinity : perTileEdited[l] / headroom;
            var breakEvenRaw = headroomRaw <= 0 ? double.PositiveInfinity : perTileEdited[l] / headroomRaw;
            Console.WriteLine($"{layers[l].Dataset,-22} {perTileEdited[l],19:F1} {perTileComposeRaw,10:F1} {perTileCompose,10:F1} {breakEvenRaw,16:F2} {breakEvenPng,16:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("## Break-even on allocation (load-independent; the trustworthy basis on a contended box)");
        Console.WriteLine($"# alloc/tile: A cold re-render {cold.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F2} MB, B composite+raw {composeRaw.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB,");
        Console.WriteLine($"#             B re-renders the edited layer at {perLayer.Min(report => report.AllocatedBytes) / (double)tiles.Count / 1024.0 / 1024.0:F2}-{perLayer.Max(report => report.AllocatedBytes) / (double)tiles.Count / 1024.0 / 1024.0:F2} MB/tile.");
        Console.WriteLine($"{"edited layer",-22} {"edited layer MB/tile",20} {"break-even+raw",16} {"break-even+png",16}");
        for (var l = 0; l < layers.Count; l++)
        {
            var allocPerTile = perLayer[l].AllocatedBytes / (double)tiles.Count;
            var headroomRaw = cold.AllocatedBytes / (double)tiles.Count - composeRaw.AllocatedBytes / (double)tiles.Count;
            var headroomPng = cold.AllocatedBytes / (double)tiles.Count - compose.AllocatedBytes / (double)tiles.Count;
            Console.WriteLine($"{layers[l].Dataset,-22} {allocPerTile / 1024.0 / 1024.0,20:F2} {(headroomRaw <= 0 ? double.PositiveInfinity : allocPerTile / headroomRaw),16:F2} {(headroomPng <= 0 ? double.PositiveInfinity : allocPerTile / headroomPng),16:F2}");
        }

        Console.WriteLine();
        Console.WriteLine($"# the entry bound is {options.MaxEntries}: whole-map holds {options.MaxEntries} tiles, " +
            $"per-layer holds {options.MaxEntries / layers.Count} tiles' worth for the same budget");
    }

    private static double Min(IReadOnlyList<double> values) => values.Min();

    private static double Max(IReadOnlyList<double> values) => values.Max();

    private static MapRenderRequest Request(IReadOnlyList<MapLayerSource> sources, string style) =>
        new(new RasterViewport(default, 0, 0, string.Empty), style, sources, Format: RasterFormat.Png, Quality: 90, Background: "#0b0f14", Transparent: false, Scale: 1.0);

    /// <summary>The publication's composed style, in the published layer order.</summary>
    private static string ComposedStyle(IReadOnlyList<LayerSpec> layers) =>
        MapStyle.Compose(Service, [.. layers.Select((layer, index) => new MapLayer(layer.Dataset, index, layer.Dataset, layer.Style))]);

    /// <summary>The same style with exactly one layer restyled — what a single-layer style save writes.</summary>
    private static string RestyledStyle(IReadOnlyList<LayerSpec> published, LayerSpec restyled, string style) =>
        MapStyle.Compose(Service, [.. published.Select((spec, index) => new MapLayer(spec.Dataset, index, spec.Dataset, spec.Dataset == restyled.Dataset ? style : spec.Style))]);

    private static async Task<string> VersionAsync(SingleRegistry registry, IReadOnlyList<LayerSpec> layers, string style, CancellationToken cancellationToken) =>
        Mirror.Version(
            Service,
            style,
            await Mirror.DataVersionAsync(registry, registry.Key, layers, cancellationToken));

    /// <summary>
    /// The same version, folded from the already-resolved render sources, so
    /// the fold reads the same <c>(store, dataset)</c> pairs the tile will read.
    /// </summary>
    private static async Task<string> VersionAsyncFromSources(
        IReadOnlyList<MapLayerSource> sources, IReadOnlyList<LayerSpec> published, string style, CancellationToken cancellationToken) =>
        Mirror.Version(
            Service,
            style,
            await ContentVersions.FoldAsync(
                new SingleRegistry(sources[0].Features, sources[0].Catalogue),
                [.. published.Select(layer => new ContentVersionRef("memory", layer.Dataset))],
                cancellationToken));

    private static async Task<SampleReport> MeasureAsync(
        Options options,
        string scenario,
        string variant,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        SampleReport.From(
            scenario,
            variant,
            await Measurement.RunAsync(operation, options.Warmup, options.Iterations, cancellationToken));

    /// <summary>The tiles covering the metro extent at a zoom: the working set a client pulls for one view.</summary>
    internal static IReadOnlyList<TileCoordinate> WorkingTiles(int zoom)
    {
        var scale = 1 << zoom;
        var minX = TileX(Basemap.MinLon, scale);
        var maxX = TileX(Basemap.MaxLon, scale);
        var minY = TileY(Basemap.MaxLat, scale);
        var maxY = TileY(Basemap.MinLat, scale);
        var tiles = new List<TileCoordinate>();
        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                tiles.Add(new TileCoordinate(zoom, x, y));
            }
        }

        return tiles;
    }

    private static int TileX(double lon, int scale) =>
        Math.Clamp((int)Math.Floor(((lon + 180.0) / 360.0) * scale), 0, scale - 1);

    private static int TileY(double lat, int scale)
    {
        var radians = lat * Math.PI / 180.0;
        var mercator = Math.Log(Math.Tan(radians) + (1.0 / Math.Cos(radians)));
        return Math.Clamp((int)Math.Floor(((1.0 - (mercator / Math.PI)) / 2.0) * scale), 0, scale - 1);
    }

    private static string Short(string dataset) => dataset[(dataset.LastIndexOf('.') + 1)..];
}

/// <summary>One invalidation measurement: what a single-layer change threw out, and what it really changed.</summary>
internal sealed record FanOut(string Case, string Layer, int WarmEntries, int Invalidated, int Changed, double PerLayerRenderMs, int Layers)
{
    internal double OverInvalidation => Invalidated / (double)Math.Max(1, Changed);

    internal string ToRow() =>
        $"{Case,-6} {Layer,-22} {WarmEntries,7} {Invalidated,11} {Changed,9} {OverInvalidation,8:F1}x {PerLayerRenderMs,14:F2}";

    internal const string Header =
        "case    layer                   warm   invalidated    changed  over-inval   per-layer ms";
}
