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
/// <summary>The run context: the store, renderer, working set and version every phase shares.</summary>
internal sealed record SpikeSetup(
    MemoryStore Store,
    SingleRegistry Registry,
    MapRenderer Renderer,
    WebMercatorTileScheme Scheme,
    CountingCache Cache,
    IReadOnlyList<LayerSpec> Layers,
    IReadOnlyList<TileCoordinate> Tiles,
    IReadOnlyList<MapLayerSource> StyleSources,
    string Style,
    string Version)
{
    internal static async Task<SpikeSetup> CreateAsync(Options options, CancellationToken cancellationToken)
    {
        var store = new MemoryStore();
        var registry = new SingleRegistry(store, store);
        var renderer = new MapRenderer(
            new ProjNetTransforms(),
            new NtsGeometryOperations(),
            new VipsRasterOperations(new Dictionary<string, string>()));
        var scheme = new WebMercatorTileScheme();
        var cache = new CountingCache(options.MaxEntries, options.MaxBytes);

        var layers = await Basemap.PublishAsync(store, Program.WorkingTiles(options.Zoom).Count, options.Density, cancellationToken);
        if (options.Layers < layers.Count)
        {
            layers = [.. layers.Take(options.Layers)];
        }

        var tiles = Program.WorkingTiles(options.Zoom);
        var styleSources = Mirror.Sources(registry, registry.Key, layers);
        var style = Program.ComposedStyle(layers);
        var version = await Program.VersionAsync(registry, layers, style, cancellationToken);
        return new SpikeSetup(store, registry, renderer, scheme, cache, layers, tiles, styleSources, style, version);
    }
}

/// <summary>Arm A outcome: the cold/warm whole-map samples and the warm working-set size.</summary>
internal sealed record ArmAResult(SampleReport Cold, SampleReport Warm, int EntriesAfterWarm, long BytesAfterWarm);

/// <summary>Arm B outcome: per-layer renders, bytes, tile stacks and composite samples.</summary>
internal sealed record ArmBResult(
    List<SampleReport> PerLayerRenders,
    List<long> PerLayerBytes,
    List<byte[]>[] PerLayerTiles,
    List<NetVips.Image>[] Decoded,
    SampleReport Compose,
    SampleReport ComposeRaw)
{
    internal void DisposeDecoded()
    {
        foreach (var stack in Decoded)
        {
            foreach (var image in stack)
            {
                image.Dispose();
            }
        }
    }
}

