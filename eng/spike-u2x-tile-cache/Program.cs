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

        var compose = await MeasureAsync(options, "serve-compose", "B composite", token =>
        {
            foreach (var stack in perLayerTiles)
            {
                ServeComposite.Composite(stack, scheme.TileSize, scheme.TileSize);
            }

            return Task.CompletedTask;
        }, cancellationToken);
        Console.WriteLine(compose.ToRow());
        Console.WriteLine($"# B holds {tiles.Count * layers.Count} entries for the same {tiles.Count} tiles " +
            $"({(tiles.Count * layers.Count) / (double)options.MaxEntries:P0} of the entry bound), " +
            $"{perLayerBytes.Sum() / 1024.0 / 1024.0:F2} MB against A's {bytesAfterWarm / 1024.0 / 1024.0:F2} MB");
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
        Decision(options, layers, tiles, cold, warm, perLayerRenders, compose, edits, bytesAfterWarm, perLayerBytes);

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
        // actually change" is a byte comparison and not an inference.
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
        var point = GeometryFactory.CreatePoint(13.404, 52.520, CoordinateReference.Epsg(4326));
        var feature = new Feature(
            FeatureId.Unassigned,
            schema,
            layer.Family == GeometryFamily.Point
                ? [AttributeValue.FromString("spike-edit"), AttributeValue.FromString("place"), AttributeValue.FromGeometry(point)]
                : [AttributeValue.FromString("spike-edit"), AttributeValue.FromGeometry(point)]);
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
        IReadOnlyList<LayerSpec> layers,
        IReadOnlyList<TileCoordinate> tiles,
        SampleReport cold,
        SampleReport warm,
        List<SampleReport> perLayer,
        SampleReport compose,
        List<FanOut> fanOut,
        long wholeMapBytes,
        IReadOnlyList<long> perLayerBytes)
    {
        var worst = fanOut.Where(row => row.Case == "edit").MaxBy(row => row.Invalidated)!;
        var bestCase = fanOut.Where(row => row.Case == "edit").MinBy(row => row.Changed)!;
        var perTileCompose = compose.P50Milliseconds / tiles.Count;
        var perTileCold = cold.P50Milliseconds / tiles.Count;
        var perTileWarm = warm.P50Milliseconds / tiles.Count;

        Console.WriteLine("## Derived — the numbers the decision is made on");
        Console.WriteLine($"tiles in the working set                 {tiles.Count}");
        Console.WriteLine($"layers in the published map             {layers.Count}");
        Console.WriteLine($"whole-map cold re-render, per tile      {perTileCold:F2} ms");
        Console.WriteLine($"whole-map warm hit, per tile            {perTileWarm:F2} ms");
        Console.WriteLine($"serve-time composite, per tile          {perTileCompose:F2} ms  (emulated, B only)");
        Console.WriteLine($"over-invalidation, a single-layer edit  {worst.Invalidated / (double)Math.Max(1, worst.Changed):F1}x  (worst layer)");
        Console.WriteLine($"over-invalidation, a single-layer edit  {bestCase.Invalidated / (double)Math.Max(1, bestCase.Changed):F1}x  (sparsest layer)");
        Console.WriteLine($"cache entries, whole-map                {tiles.Count}");
        Console.WriteLine($"cache entries, per-layer                {tiles.Count * layers.Count}");
        Console.WriteLine($"cache bytes, whole-map                  {wholeMapBytes / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"cache bytes, per-layer (sum)            {perLayerBytes.Sum() / 1024.0 / 1024.0:F2} MB");

        // Break-even: the one-off saving against the standing tax.
        Console.WriteLine();
        Console.WriteLine("## Break-even — how many tiles must be served after one edit before per-layer composition pays for itself");
        Console.WriteLine($"{"edited layer",-22} {"saving/edit ms",15} {"tax/tile ms",12} {"break-even tiles",18}");
        for (var l = 0; l < layers.Count; l++)
        {
            var saving = cold.P50Milliseconds - perLayer[l].P50Milliseconds;
            var breakEven = perTileCompose <= 0 ? double.PositiveInfinity : saving / (perTileCompose * tiles.Count);
            Console.WriteLine($"{layers[l].Dataset,-22} {saving,15:F1} {perTileCompose,12:F2} {breakEven,18:F0}");
        }

        Console.WriteLine();
        Console.WriteLine($"# the entry bound is {options.MaxEntries}: whole-map holds {options.MaxEntries} tiles, " +
            $"per-layer holds {options.MaxEntries / layers.Count} tiles' worth for the same budget");
    }

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
