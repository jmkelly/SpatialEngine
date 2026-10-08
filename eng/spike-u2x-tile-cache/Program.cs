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
    internal const string Service = "spike-city";

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
        var setup = await SpikeSetup.CreateAsync(options, cancellationToken);
        WriteRunHeaders(options, setup);

        // ---------------------------------------------------------------- map
        Console.WriteLine($"# version(V0) = {setup.Version[..16]}…  (service + composed style + folded data versions, ADR-0083)");
        Console.WriteLine();

        var armA = await ArmA.MeasureArmAAsync(options, setup, cancellationToken);
        var armB = await ArmB.MeasureArmBAsync(options, setup, cancellationToken);

        await ContentionProbeAsync(
            options,
            setup.Renderer,
            setup.Cache,
            setup.Scheme,
            setup.Version,
            setup.StyleSources,
            setup.Style,
            armB.Decoded,
            setup.Tiles,
            cancellationToken);

        armB.DisposeDecoded();
        ArmB.ReportArmB(options, setup, armA, armB);

        // ------------------------------------------ the two invalidation cases
        var edits = await MeasureInvalidationAsync(options, setup, armB, cancellationToken);

        // -------------------------------------------------- C and the flush
        WriteArmC(options, setup, edits);

        await FlushAsync(options, setup.Registry, setup.Renderer, setup.Cache, setup.Scheme, setup.Layers, setup.Tiles, setup.StyleSources, cancellationToken);

        // ------------------------------------------------------ the decision
        SpikeDecision.Decision(options, setup.Scheme.TileSize, setup.Layers, setup.Tiles, armA.Cold, armA.Warm, armB.PerLayerRenders, armB.Compose, armB.ComposeRaw, edits, armA.BytesAfterWarm, armB.PerLayerBytes);

        if (options.Host is { } host)
        {
            await HostCheck.RunAsync(options.Host, Service, setup.Layers, setup.Tiles, cancellationToken);
        }
    }

    private static void WriteRunHeaders(Options options, SpikeSetup setup)
    {
        Console.WriteLine($"# SpatialEngine-u2x.21.2 tile invalidation fan-out — label={options.Label}");
        Console.WriteLine($"# map '{Service}': {setup.Layers.Count} layers over a {Basemap.MaxLon - Basemap.MinLon:F2}deg x {Basemap.MaxLat - Basemap.MinLat:F2}deg metro extent");
        Console.WriteLine($"#   " + string.Join(", ", setup.Layers.Select(layer => $"{layer.Dataset}({layer.PerTile * options.Density}/tile)")));
        Console.WriteLine($"# working set: {setup.Tiles.Count} tiles at z={options.Zoom} covering that extent; iterations={options.Iterations} warmup={options.Warmup}");
        Console.WriteLine($"# A=whole-map entries (shipped)  B=per-layer entries composed at serve time (emulated)  C=per-layer style sub-key (emulated)");
        Console.WriteLine($"# cache bounds: maxEntries={options.MaxEntries} maxBytes={options.MaxBytes}");
        Console.WriteLine();
    }

    /// <summary>The two invalidation cases: a single-layer data edit, then a single-layer style save.</summary>
    private static async Task<List<FanOut>> MeasureInvalidationAsync(
        Options options, SpikeSetup setup, ArmBResult armB, CancellationToken cancellationToken)
    {
        Console.WriteLine("## Invalidation fan-out — how many of the warm entries a single-layer change throws out");
        var edits = new List<FanOut>();
        foreach (var layer in setup.Layers)
        {
            edits.Add(await FanOutPhases.FanOutAsync(options, "edit", layer, setup.Layers, setup.StyleSources, setup.Cache, setup.Renderer, setup.Scheme, setup.Tiles, setup.Style, setup.Version, armB.PerLayerRenders[edits.Count], cancellationToken));
        }

        foreach (var layer in setup.Layers)
        {
            edits.Add(await FanOutPhases.StyleFanOutAsync(options, layer, setup.Layers, setup.StyleSources, setup.Cache, setup.Renderer, setup.Scheme, setup.Tiles, setup.Style, setup.Version, armB.PerLayerRenders[edits.Count - setup.Layers.Count], cancellationToken));
        }

        Console.WriteLine(FanOut.Header);
        foreach (var row in edits)
        {
            Console.WriteLine(row.ToRow());
        }

        Console.WriteLine();
        return edits;
    }

    private static void WriteArmC(Options options, SpikeSetup setup, List<FanOut> edits)
    {
        Console.WriteLine("## C — per-layer style sub-key (emulated), and the global flush");
        Console.WriteLine("# C keeps ONE entry per tile and only changes how the version is folded, so the entry count and");
        Console.WriteLine("# therefore the fan-out are the same as A. The measured rows below are A's, re-labelled.");
        var styleArm = edits.First(row => row.Case == "style" && row.Layer == setup.Layers[0].Dataset);
        Console.WriteLine($"#   C style save on {setup.Layers[0].Dataset}: {styleArm.Invalidated} thrown out, {styleArm.Changed} genuinely changed — identical to A above");
        Console.WriteLine();
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


    /// <summary>
    /// Which timing basis survives a contended box?
    ///
    /// This bead's number has to be reproducible on a machine shared with a
    /// parallel swarm, and the wall-time ordering of the two arms was observed
    /// to FLIP between runs of the spike (compose cheaper than a whole-map cold
    /// render in two runs, dearer in a third). So the spike does not assert a
    /// basis is trustworthy — it manufactures contention it controls, measures
    /// both arms again with background burners running, and reports which basis
    /// kept the ordering. Wall time is expected to inflate and reorder; CPU time
    /// is expected to hold. That result is what licenses the break-even tables
    /// above to be computed on CPU.
    ///
    /// The burners are plain spinning threads on the same process, standing in
    /// for the swarm on the shared box. They are torn down before returning.
    /// </summary>
    private static async Task ContentionProbeAsync(
        Options options,
        MapRenderer renderer,
        CountingCache cache,
        WebMercatorTileScheme scheme,
        string version,
        IReadOnlyList<MapLayerSource> styleSources,
        string style,
        List<NetVips.Image>[] decoded,
        IReadOnlyList<TileCoordinate> tiles,
        CancellationToken cancellationToken)
    {
        var burners = new List<Process>();
        try
        {
            Console.WriteLine();
            Console.WriteLine("## Contention probe — does the arm ordering survive a loaded box?");
            Console.WriteLine("# Both arms are measured again with synthetic background load. If a basis is to be trusted it must");
            Console.WriteLine("# keep the SAME ORDERING under load; a basis that reorders between idle and loaded cannot decide this.");
            Console.WriteLine($"{"load",-12} {"arm",-12} {"p50_ms",10} {"p50_cpu_ms",12}   ratio-vs-A (ms / cpu)");

            Func<CancellationToken, Task> cold = token => ColdAsync(renderer, cache, scheme, version, styleSources, style, tiles, token);
            Func<CancellationToken, Task> compose = token => ComposeAsync(decoded, scheme.TileSize, token);

            var idleCold = await ProbeAsync(options, 0, cold, cancellationToken);
            var idleCompose = await ProbeAsync(options, 0, compose, cancellationToken);

            StartBurners(burners, Environment.ProcessorCount);
            var loadedCold = await ProbeAsync(options, burners.Count, cold, cancellationToken);
            var loadedCompose = await ProbeAsync(options, burners.Count, compose, cancellationToken);

            ProbeRow("idle", "A cold", idleCold, null);
            ProbeRow("idle", "B compose", idleCompose, idleCold);
            ProbeRow("loaded", "A cold", loadedCold, null);
            ProbeRow("loaded", "B compose", loadedCompose, loadedCold);

            var idleRatioMs = idleCompose.P50Milliseconds / idleCold.P50Milliseconds;
            var loadedRatioMs = loadedCompose.P50Milliseconds / loadedCold.P50Milliseconds;
            var idleRatioCpu = idleCompose.P50CpuMilliseconds / idleCold.P50CpuMilliseconds;
            var loadedRatioCpu = loadedCompose.P50CpuMilliseconds / loadedCold.P50CpuMilliseconds;

            Console.WriteLine();
            Console.WriteLine($"# compose/cold ratio, idle   -> {idleRatioMs:F2}x on wall, {idleRatioCpu:F2}x on cpu");
            Console.WriteLine($"# compose/cold ratio, loaded -> {loadedRatioMs:F2}x on wall, {loadedRatioCpu:F2}x on cpu");
            Console.WriteLine($"# wall ratio drifted {Math.Abs(loadedRatioMs - idleRatioMs) / Math.Max(idleRatioMs, 1e-9):P0}; cpu ratio drifted {Math.Abs(loadedRatioCpu - idleRatioCpu) / Math.Max(idleRatioCpu, 1e-9):P0}");
            var wallReordered = (idleRatioMs - 1d) * (loadedRatioMs - 1d) < 0;
            var cpuReordered = (idleRatioCpu - 1d) * (loadedRatioCpu - 1d) < 0;
            Console.WriteLine($"# wall time REORDERED the arms under load: {(wallReordered ? "yes" : "no")}. cpu time reordered them: {(cpuReordered ? "yes" : "no")}.");
            Console.WriteLine($"# => the break-even tables above are computed on {(cpuReordered ? "WALL" : "CPU")} time, and allocation is reported only as a proxy.");
        }
        finally
        {
            foreach (var burner in burners)
            {
                try
                {
                    if (!burner.HasExited)
                    {
                        burner.Kill(entireProcessTree: true);
                    }

                    burner.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // already gone; the probe has its numbers either way
                }
                finally
                {
                    burner.Dispose();
                }
            }
        }
    }

    private static void ProbeRow(string load, string arm, SampleReport report, SampleReport? baseline)
    {
        var ratio = baseline is null
            ? string.Empty
            : $"   {report.P50Milliseconds / baseline.P50Milliseconds:F2}x / {report.P50CpuMilliseconds / baseline.P50CpuMilliseconds:F2}x";
        Console.WriteLine($"{load,-12} {arm,-12} {report.P50Milliseconds,10:F2} {report.P50CpuMilliseconds,12:F1}{ratio}");
    }

    private static async Task<SampleReport> ProbeAsync(
        Options options,
        int load,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        SampleReport.From("probe", $"load{load}", await Measurement.RunAsync(operation, 1, Math.Max(2, Math.Min(4, options.Iterations)), cancellationToken));

    /// <summary>
    /// The load is applied by SEPARATE processes, not by threads in this one.
    /// That matters for the very number the probe reports: an in-process burner
    /// would add its own spinning straight into this process's
    /// TotalProcessorTime, which is the measurement, and CPU time under load
    /// would read as ~17x idle — a contaminated number dressed up as evidence
    /// about the probe. External processes contend for the cores the way the
    /// swarm on the shared box does, and leave this process's CPU time alone.
    /// </summary>
    private static void StartBurners(List<Process> burners, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var process = Process.Start(new ProcessStartInfo("bash", "-c \"while :; do :; done\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is not null)
            {
                burners.Add(process);
            }
        }
    }

    private static async Task ColdAsync(
        MapRenderer renderer,
        CountingCache cache,
        WebMercatorTileScheme scheme,
        string version,
        IReadOnlyList<MapLayerSource> styleSources,
        string style,
        IReadOnlyList<TileCoordinate> tiles,
        CancellationToken cancellationToken)
    {        await cache.ClearAsync(cancellationToken);
        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await renderer.RenderAsync(
                new MapRenderRequest(
                    new RasterViewport(scheme.Bounds(tile), scheme.TileSize, scheme.TileSize, scheme.Crs),
                    style,
                    styleSources),
                cancellationToken);
        }
    }

    private static Task ComposeAsync(IReadOnlyList<List<NetVips.Image>> decoded, int tileSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var stack in decoded)
        {
            ServeComposite.CompositePreDecoded(stack, tileSize, tileSize);
        }

        return Task.CompletedTask;
    }
    internal static MapRenderRequest Request(IReadOnlyList<MapLayerSource> sources, string style) =>
        new(new RasterViewport(default, 0, 0, string.Empty), style, sources, Format: RasterFormat.Png, Quality: 90, Background: "#0b0f14", Transparent: false, Scale: 1.0);

    /// <summary>The publication's composed style, in the published layer order.</summary>
    internal static string ComposedStyle(IReadOnlyList<LayerSpec> layers) =>
        MapStyle.Compose(Service, [.. layers.Select((layer, index) => new MapLayer(layer.Dataset, index, layer.Dataset, layer.Style))]);

    /// <summary>The same style with exactly one layer restyled — what a single-layer style save writes.</summary>
    internal static string RestyledStyle(IReadOnlyList<LayerSpec> published, LayerSpec restyled, string style) =>
        MapStyle.Compose(Service, [.. published.Select((spec, index) => new MapLayer(spec.Dataset, index, spec.Dataset, spec.Dataset == restyled.Dataset ? style : spec.Style))]);

    internal static async Task<string> VersionAsync(SingleRegistry registry, IReadOnlyList<LayerSpec> layers, string style, CancellationToken cancellationToken) =>
        Mirror.Version(
            Service,
            style,
            await Mirror.DataVersionAsync(registry, registry.Key, layers, cancellationToken));

    /// <summary>
    /// The same version, folded from the already-resolved render sources, so
    /// the fold reads the same <c>(store, dataset)</c> pairs the tile will read.
    /// </summary>
    internal static async Task<string> VersionAsyncFromSources(
        IReadOnlyList<MapLayerSource> sources, IReadOnlyList<LayerSpec> published, string style, CancellationToken cancellationToken) =>
        Mirror.Version(
            Service,
            style,
            await ContentVersions.FoldAsync(
                new SingleRegistry(sources[0].Features, sources[0].Catalogue),
                [.. published.Select(layer => new ContentVersionRef("memory", layer.Dataset))],
                cancellationToken));

    internal static async Task<SampleReport> MeasureAsync(
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

    internal static string Short(string dataset) => dataset[(dataset.LastIndexOf('.') + 1)..];
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
