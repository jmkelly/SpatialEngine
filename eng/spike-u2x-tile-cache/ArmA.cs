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
/// <summary>Arm A: whole-map cache entries (the shipped design).</summary>
internal static class ArmA
{
/// <summary>Arm A: whole-map cache entries (the shipped design).</summary>
internal static async Task<ArmAResult> MeasureArmAAsync(Options options, SpikeSetup setup, CancellationToken cancellationToken)
{
    Console.WriteLine("## A — whole-map cache entries (the shipped design)");
    var cold = await Program.MeasureAsync(options, "cold-render", "A whole-map", token =>
        RenderWholeMapAsync(setup, clearFirst: true, token), cancellationToken);
    var warm = await Program.MeasureAsync(options, "warm-hit", "A whole-map", token =>
        RenderWholeMapAsync(setup, clearFirst: false, token), cancellationToken);

    var entriesAfterWarm = setup.Cache.Entries;
    var bytesAfterWarm = setup.Cache.Bytes;
    Console.WriteLine(SampleReport.Header);
    Console.WriteLine(cold.ToRow());
    Console.WriteLine(warm.ToRow());
    Console.WriteLine($"# warm working set: {entriesAfterWarm} entries, {bytesAfterWarm / 1024.0 / 1024.0:F2} MB, {bytesAfterWarm / (double)entriesAfterWarm / 1024.0:F1} KB/tile average");
    Console.WriteLine();
    return new ArmAResult(cold, warm, entriesAfterWarm, bytesAfterWarm);
}

private static async Task RenderWholeMapAsync(SpikeSetup setup, bool clearFirst, CancellationToken cancellationToken)
{
    if (clearFirst)
    {
        await setup.Cache.ClearAsync(cancellationToken);
    }

    foreach (var tile in setup.Tiles)
    {
        await Mirror.RenderAsync(setup.Renderer, setup.Cache, setup.Scheme, tile, setup.Version, Program.Request(setup.StyleSources, setup.Style), cancellationToken);
    }
}
}
