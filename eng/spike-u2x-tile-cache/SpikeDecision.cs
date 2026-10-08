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
/// <summary>The decision, stated from the numbers the spike just produced.</summary>
internal static class SpikeDecision
{
/// <summary>The decision, stated from the numbers the spike just produced.</summary>
internal static void Decision(
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
    // The same three quantities on CPU time, which is the basis the
    // decision is made on (see the contention probe below).
    var cpu = ComputeCpuBasis(cold, warm, compose, composeRaw, tiles);
    var edited = PerTileEdited(perLayer, tiles);

    WriteDerivedHeader(tiles, layers, cold, warm, compose, composeRaw, fanOut, cpu);
    WriteCacheBytes(options, tileSize, layers, tiles, wholeMapBytes, perLayerBytes);
    WriteBreakEvenCpu(layers, cold, compose, composeRaw, perLayer, edited, cpu);
    WriteBreakEvenAlloc(layers, tiles, cold, compose, composeRaw, perLayer);
    WriteAmortisedTotals(options, layers, tiles, edited, cpu);
}

/// <summary>Per-tile CPU/wall figures both break-even tables share.</summary>
private sealed record CpuBasis(
    double PerTileCompose, double PerTileComposeRaw, double PerTileCold, double PerTileWarm,
    double CpuCompose, double CpuComposeRaw, double CpuCold, double CpuWarm);

private static CpuBasis ComputeCpuBasis(SampleReport cold, SampleReport warm, SampleReport compose, SampleReport composeRaw, IReadOnlyList<TileCoordinate> tiles) =>
    new(
        compose.P50Milliseconds / tiles.Count,
        composeRaw.P50Milliseconds / tiles.Count,
        cold.P50Milliseconds / tiles.Count,
        warm.P50Milliseconds / tiles.Count,
        compose.P50CpuMilliseconds / tiles.Count,
        composeRaw.P50CpuMilliseconds / tiles.Count,
        cold.P50CpuMilliseconds / tiles.Count,
        warm.P50CpuMilliseconds / tiles.Count);

/// <summary>Per-layer re-render cost, per tile, on both bases.</summary>
private sealed record EditedCost(double[] PerTileEdited, double[] PerTileEditedCpu);

private static EditedCost PerTileEdited(List<SampleReport> perLayer, IReadOnlyList<TileCoordinate> tiles)
{
    var perTileEdited = new double[perLayer.Count];
    var perTileEditedCpu = new double[perLayer.Count];
    for (var l = 0; l < perLayer.Count; l++)
    {
        perTileEdited[l] = perLayer[l].P50Milliseconds / tiles.Count;
        perTileEditedCpu[l] = perLayer[l].P50CpuMilliseconds / tiles.Count;
    }

    return new EditedCost(perTileEdited, perTileEditedCpu);
}

private static void WriteDerivedHeader(
    IReadOnlyList<TileCoordinate> tiles,
    IReadOnlyList<LayerSpec> layers,
    SampleReport cold,
    SampleReport warm,
    SampleReport compose,
    SampleReport composeRaw,
    List<FanOut> fanOut,
    CpuBasis cpu)
{
    var worst = fanOut.Where(row => row.Case == "edit").MaxBy(row => row.Invalidated)!;
    var bestCase = fanOut.Where(row => row.Case == "edit").MinBy(row => row.Changed)!;

    Console.WriteLine("## Derived — the numbers the decision is made on");
    Console.WriteLine("# wall_ms is reported for continuity, but CPU_ms is the decision basis: the wall-time ORDERING of");
    Console.WriteLine("# these arms flipped with box load across three runs, while CPU time held (see the probe at the end).");
    Console.WriteLine($"tiles in the working set                 {tiles.Count}");
    Console.WriteLine($"layers in the published map             {layers.Count}");
    Console.WriteLine($"whole-map warm hit, per tile            {cpu.PerTileWarm:F2} ms wall   {cpu.CpuWarm:F2} ms cpu   alloc {(warm.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0):F3} MB");
    Console.WriteLine($"whole-map cold re-render, per tile      {cpu.PerTileCold:F2} ms wall   {cpu.CpuCold:F2} ms cpu   alloc {cold.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F2} MB");
    Console.WriteLine($"serve-time composite+png, per tile      {cpu.PerTileCompose:F2} ms wall   {cpu.CpuCompose:F2} ms cpu   alloc {compose.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB");
    Console.WriteLine($"serve-time composite+raw, per tile      {cpu.PerTileComposeRaw:F2} ms wall   {cpu.CpuComposeRaw:F2} ms cpu   alloc {composeRaw.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB");
    Console.WriteLine($"over-invalidation, a single-layer edit  {worst.Invalidated / (double)Math.Max(1, worst.Changed):F1}x  (worst layer)");
    Console.WriteLine($"over-invalidation, a single-layer edit  {bestCase.Invalidated / (double)Math.Max(1, bestCase.Changed):F1}x  (sparsest layer)");
    var styleRows = fanOut.Where(row => row.Case == "style").ToList();
    Console.WriteLine($"over-invalidation, a single-layer style {styleRows.Min(row => row.Invalidated / (double)Math.Max(1, row.Changed)):F1}x  (min across layers — every tile's pixels really did change)");
    Console.WriteLine();
    Console.WriteLine("## Read the over-invalidation factor with the cache's laziness in mind");
    Console.WriteLine("# The fan-out counts warm entries DROPPED, not tiles re-rendered. Both arms are demand-filled: the");
    Console.WriteLine("# host's TileService (and this spike's Mirror.RenderAsync) look the key up and render only on a miss,");
    Console.WriteLine("# so a version move makes a tile cost something only if a client asks for that tile again.");
    Console.WriteLine($"# So the price of a single-layer edit is (tiles re-requested) x {cpu.PerTileCold:F1} ms, not {tiles.Count} x that. This harness");
    Console.WriteLine("# re-requests the whole working set to count the fan-out, which is the measurement's demand, not");
    Console.WriteLine("# the host's behaviour. The over-invalidation factor is an upper bound on the waste, not the bill.");
}

private static void WriteCacheBytes(
    Options options,
    int tileSize,
    IReadOnlyList<LayerSpec> layers,
    IReadOnlyList<TileCoordinate> tiles,
    long wholeMapBytes,
    IReadOnlyList<long> perLayerBytes)
{
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
}

/// <summary>Break-even on CPU time (the decision basis): tiles re-requested after one edit before arm B repays itself.</summary>
private static void WriteBreakEvenCpu(
    IReadOnlyList<LayerSpec> layers,
    SampleReport cold,
    SampleReport compose,
    SampleReport composeRaw,
    List<SampleReport> perLayer,
    EditedCost edited,
    CpuBasis cpu)
{

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
    //   arm A: K x cpu.PerTileCold  (every one of them is a cache miss)
    //   arm B: edited.PerTileEditedLayer (re-render the one edited layer, once)
    //           + K x tax        (blend and encode on every serve)
    // Break-even solves  edited.PerTileEditedLayer + K x tax = K x cpu.PerTileCold,
    // which is a LOWER bound being a better result for arm B: the fewer
    // tiles must be re-requested before B repays itself, the more B is
    // worth building. An infinity means B never repays, at any load.
    Console.WriteLine();
    Console.WriteLine("## Break-even — tiles re-requested after one edit before per-layer composition repays itself");
    Console.WriteLine("# LOWER is better: the number of re-requested tiles arm B needs before it repays itself. ∞ = never repays.");
    Console.WriteLine("# Computed on CPU time (the decision basis). The wall-time version is below, unlabelled as authoritative,");
    Console.WriteLine("# because its ordering flipped with box load across the three runs of this spike.");
    Console.WriteLine($"# per tile, CPU:  A pays {cpu.CpuCold:F1} ms per re-requested tile (all cold); B pays {Min(edited.PerTileEditedCpu):F1}-{Max(edited.PerTileEditedCpu):F1} ms once to re-render the edited layer,");
    Console.WriteLine($"#                then {cpu.CpuComposeRaw:F1} ms (+raw) or {cpu.CpuCompose:F1} ms (+png) per tile served.");
    Console.WriteLine($"{"edited layer",-22} {"edited layer cpu",17} {"tax+raw",9} {"tax+png",9} {"BE+raw",9} {"BE+png",9}   (wall: BE+raw / BE+png)");
    for (var l = 0; l < layers.Count; l++)
    {
        var cpuHeadroom = cpu.CpuCold - cpu.CpuCompose;
        var cpuHeadroomRaw = cpu.CpuCold - cpu.CpuComposeRaw;
        var bePng = cpuHeadroom <= 0 ? double.PositiveInfinity : edited.PerTileEditedCpu[l] / cpuHeadroom;
        var beRaw = cpuHeadroomRaw <= 0 ? double.PositiveInfinity : edited.PerTileEditedCpu[l] / cpuHeadroomRaw;
        var wallHeadroom = cpu.PerTileCold - cpu.PerTileCompose;
        var wallHeadroomRaw = cpu.PerTileCold - cpu.PerTileComposeRaw;
        var wallPng = wallHeadroom <= 0 ? double.PositiveInfinity : edited.PerTileEdited[l] / wallHeadroom;
        var wallRaw = wallHeadroomRaw <= 0 ? double.PositiveInfinity : edited.PerTileEdited[l] / wallHeadroomRaw;
        Console.WriteLine($"{layers[l].Dataset,-22} {edited.PerTileEditedCpu[l],17:F1} {cpu.CpuComposeRaw,9:F1} {cpu.CpuCompose,9:F1} {Format(beRaw),9} {Format(bePng),9}   {Format(wallRaw)} / {Format(wallPng)}");
    }

}

/// <summary>Break-even on allocation (load-independent proxy, reported for completeness).</summary>
private static void WriteBreakEvenAlloc(
    IReadOnlyList<LayerSpec> layers,
    IReadOnlyList<TileCoordinate> tiles,
    SampleReport cold,
    SampleReport compose,
    SampleReport composeRaw,
    List<SampleReport> perLayer)
{
    Console.WriteLine();
    Console.WriteLine("## Break-even on allocation (load-independent, but a PROXY — reported for completeness)");
    Console.WriteLine("# Allocation is load-independent and exactly reproducible, but it is not the cost: the composite's");
    Console.WriteLine("# expense is Skia blend + PNG encode, which is CPU in existing buffers and allocates almost nothing.");
    Console.WriteLine("# So this basis flatters arm B and is NOT the one the decision rests on. It is kept because it is the");
    Console.WriteLine("# only exactly-reproducible column, and its disagreement with the CPU basis is itself the finding:");
    Console.WriteLine("# the two arms' ranking flips between allocation and CPU, so allocation cannot stand in for cost here.");
    Console.WriteLine($"# alloc/tile: A cold re-render {cold.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F2} MB, B composite+raw {composeRaw.AllocatedBytes / (double)tiles.Count / 1024.0 / 1024.0:F3} MB,");
    Console.WriteLine($"#             B re-renders the edited layer at {perLayer.Min(report => report.AllocatedBytes) / (double)tiles.Count / 1024.0 / 1024.0:F2}-{perLayer.Max(report => report.AllocatedBytes) / (double)tiles.Count / 1024.0 / 1024.0:F2} MB/tile.");
    Console.WriteLine($"{"edited layer",-22} {"edited layer MB/tile",20} {"break-even+raw",16} {"break-even+png",16}");
    for (var l = 0; l < layers.Count; l++)
    {
        var allocPerTile = perLayer[l].AllocatedBytes / (double)tiles.Count;
        var headroomRaw = cold.AllocatedBytes / (double)tiles.Count - composeRaw.AllocatedBytes / (double)tiles.Count;
        var headroomPng = cold.AllocatedBytes / (double)tiles.Count - compose.AllocatedBytes / (double)tiles.Count;
        Console.WriteLine($"{layers[l].Dataset,-22} {allocPerTile / 1024.0 / 1024.0,20:F2} {Format(headroomRaw <= 0 ? double.PositiveInfinity : allocPerTile / headroomRaw),16} {Format(headroomPng <= 0 ? double.PositiveInfinity : allocPerTile / headroomPng),16}");
    }

}

/// <summary>Amortised total cost at several serve/edit ratios, plus the entry-bound note.</summary>
private static void WriteAmortisedTotals(
    Options options,
    IReadOnlyList<LayerSpec> layers,
    IReadOnlyList<TileCoordinate> tiles,
    EditedCost edited,
    CpuBasis cpu)
{
    Console.WriteLine();
    Console.WriteLine("## Amortised total cost — what a deployment actually pays");
    Console.WriteLine("# The break-even above is 'tiles served per edit'. Read it that way: it is R, the ratio of tile");
    Console.WriteLine("# serves to edits over the life of a cache. Below, R is the working set (a client pulls all 25 tiles");
    Console.WriteLine("# of its viewport between edits), which is the realistic profile for an editing session.");
    Console.WriteLine("#   arm A: every one of the R serves after an edit is a cold whole-map render. R x cold.");
    Console.WriteLine("#   arm B: re-render the edited layer once, then every serve pays the composite tax.");
    Console.WriteLine($"#           editedLayer + R x tax.");
    Console.WriteLine($"{"tiles served per edit (R)",26} {"A total cpu ms",16} {"B total cpu ms (+raw)",22} {"B (+png)",14}   winner");
    foreach (var ratio in new[] { 1, 2, 5, tiles.Count, 100, 1000 })
    {
        var a = ratio * cpu.CpuCold;
        var bRaw = Max(edited.PerTileEditedCpu) + (ratio * cpu.CpuComposeRaw);
        var bPng = Max(edited.PerTileEditedCpu) + (ratio * cpu.CpuCompose);
        var winner = bRaw < a && bPng < a ? "B (both)" : bRaw < a ? "B (+raw only)" : "A";
        Console.WriteLine($"{ratio,26} {a,16:F1} {bRaw,22:F1} {bPng,14:F1}   {winner}");
    }

    Console.WriteLine();
    Console.WriteLine($"# the entry bound is {options.MaxEntries}: whole-map holds {options.MaxEntries} tiles, " +
        $"per-layer holds {options.MaxEntries / layers.Count} tiles' worth for the same budget");
}

/// <summary>Break-even figures print as a plain number, or "inf" for never.</summary>
private static string Format(double breakEven) =>
    double.IsPositiveInfinity(breakEven) ? "inf" : breakEven.ToString("F2", CultureInfo.InvariantCulture);

private static double Min(IReadOnlyList<double> values) => values.Min();

private static double Max(IReadOnlyList<double> values) => values.Max();
}
