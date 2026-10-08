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
/// <summary>Arm B: per-layer entries rendered and composed at serve time (emulated, not shipped).</summary>
internal static class ArmB
{
/// <summary>Arm B: per-layer entries rendered and composed at serve time (emulated, not shipped).</summary>
internal static async Task<ArmBResult> MeasureArmBAsync(Options options, SpikeSetup setup, CancellationToken cancellationToken)
{
    Console.WriteLine("## B — per-layer cache entries, composed at serve time (emulated, not shipped)");
    var perLayerRenders = new List<SampleReport>();
    var perLayerBytes = new List<long>();
    foreach (var layer in setup.Layers)
    {
        perLayerRenders.Add(await MeasureLayerAsync(options, setup, layer, cancellationToken));
        perLayerBytes.Add(await BytesForLayerAsync(setup, layer, cancellationToken));
    }

    Console.WriteLine(SampleReport.Header);
    foreach (var report in perLayerRenders)
    {
        Console.WriteLine(report.ToRow());
    }

    // The per-layer design's standing cost: composite the per-layer tiles
    // into the served tile, on every request, forever.
    var perLayerTiles = await RenderPerLayerTilesAsync(setup, cancellationToken);

    var compose = await Program.MeasureAsync(options, "serve-compose", "B composite+png", token =>
    {
        foreach (var stack in perLayerTiles)
        {
            ServeComposite.Composite(stack, setup.Scheme.TileSize, setup.Scheme.TileSize);
        }

        return Task.CompletedTask;
    }, cancellationToken);
    Console.WriteLine(compose.ToRow());

    // The optimistic arm: the same blend and encode, with the per-layer
    // rasters already decoded, which is what a per-layer cache holding raw
    // buffers would cost. The decode is hoisted out of the timed region
    // deliberately — it is the first arm's job to measure it.
    var decoded = DecodeStacks(setup, perLayerTiles);

    var composeRaw = await Program.MeasureAsync(options, "serve-compose", "B composite+raw", token =>
    {
        foreach (var stack in decoded)
        {
            ServeComposite.CompositePreDecoded(stack, setup.Scheme.TileSize, setup.Scheme.TileSize);
        }

        return Task.CompletedTask;
    }, cancellationToken);
    Console.WriteLine(composeRaw.ToRow());
    return new ArmBResult(perLayerRenders, perLayerBytes, perLayerTiles, decoded, compose, composeRaw);
}

private static Task<SampleReport> MeasureLayerAsync(Options options, SpikeSetup setup, LayerSpec layer, CancellationToken cancellationToken)
{
    var only = new List<LayerSpec> { layer };
    var layerSources = Mirror.Sources(setup.Registry, setup.Registry.Key, only);
    var layerStyle = MapStyle.Compose(Program.Service, [new MapLayer(layer.Dataset, 0, layer.Dataset, layer.Style)]);
    return Program.MeasureAsync(options, "cold-render", $"B {Program.Short(layer.Dataset)}", async token =>
    {
        foreach (var tile in setup.Tiles)
        {
            await setup.Renderer.RenderAsync(
                new MapRenderRequest(
                    new RasterViewport(setup.Scheme.Bounds(tile), setup.Scheme.TileSize, setup.Scheme.TileSize, setup.Scheme.Crs),
                    layerStyle,
                    layerSources),
                token);
        }
    }, cancellationToken);
}

private static async Task<long> BytesForLayerAsync(SpikeSetup setup, LayerSpec layer, CancellationToken cancellationToken)
{
    var only = new List<LayerSpec> { layer };
    var layerSources = Mirror.Sources(setup.Registry, setup.Registry.Key, only);
    var layerStyle = MapStyle.Compose(Program.Service, [new MapLayer(layer.Dataset, 0, layer.Dataset, layer.Style)]);

    // The bytes one layer's tile would occupy, for the cache-size argument.
    long bytes = 0;
    foreach (var tile in setup.Tiles)
    {
        var image = await setup.Renderer.RenderAsync(
            new MapRenderRequest(
                new RasterViewport(setup.Scheme.Bounds(tile), setup.Scheme.TileSize, setup.Scheme.TileSize, setup.Scheme.Crs),
                layerStyle,
                layerSources),
            cancellationToken);
        bytes += image.Content.LongLength;
    }

    return bytes;
}

private static async Task<List<byte[]>[]> RenderPerLayerTilesAsync(SpikeSetup setup, CancellationToken cancellationToken)
{
    var perLayerTiles = new List<byte[]>[setup.Tiles.Count];
    for (var t = 0; t < setup.Tiles.Count; t++)
    {
        perLayerTiles[t] = new List<byte[]>(setup.Layers.Count);
    }

    for (var l = 0; l < setup.Layers.Count; l++)
    {
        var only = new List<LayerSpec> { setup.Layers[l] };
        var layerSources = Mirror.Sources(setup.Registry, setup.Registry.Key, only);
        var layerStyle = MapStyle.Compose(Program.Service, [new MapLayer(setup.Layers[l].Dataset, 0, setup.Layers[l].Dataset, setup.Layers[l].Style)]);
        for (var t = 0; t < setup.Tiles.Count; t++)
        {
            var image = await setup.Renderer.RenderAsync(
                new MapRenderRequest(
                    new RasterViewport(setup.Scheme.Bounds(setup.Tiles[t]), setup.Scheme.TileSize, setup.Scheme.TileSize, setup.Scheme.Crs),
                    layerStyle,
                    layerSources),
                cancellationToken);
            perLayerTiles[t].Add(image.Content);
        }
    }

    return perLayerTiles;
}

private static List<NetVips.Image>[] DecodeStacks(SpikeSetup setup, List<byte[]>[] perLayerTiles)
{
    var decoded = new List<NetVips.Image>[setup.Tiles.Count];
    for (var t = 0; t < setup.Tiles.Count; t++)
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

    return decoded;
}

internal static void ReportArmB(Options options, SpikeSetup setup, ArmAResult armA, ArmBResult armB)
{
    Console.WriteLine();
    Console.WriteLine($"# B holds {setup.Tiles.Count * setup.Layers.Count} entries for the same {setup.Tiles.Count} tiles " +
        $"({(setup.Tiles.Count * setup.Layers.Count) / (double)options.MaxEntries:P0} of the entry bound), " +
        $"{armB.PerLayerBytes.Sum() / 1024.0 / 1024.0:F2} MB against A's {armA.BytesAfterWarm / 1024.0 / 1024.0:F2} MB");
    Console.WriteLine("# 'composite+png' decodes each cached layer on every request (cache stores PNG);");
    Console.WriteLine("# 'composite+raw' is the same blend and encode with the layers already decoded (cache stores raw buffers).");
    Console.WriteLine();
}
}
