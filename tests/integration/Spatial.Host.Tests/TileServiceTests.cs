using Spatial.Contracts;
using Spatial.Host.TileServing;
using Spatial.Tiling.WebMercator;

namespace Spatial.Host.Tests;

/// <summary>
/// The tile orchestration (ADR-0046): cache-aware single-tile rendering,
/// scheme resolution, batch order, the batch cap and cancellation.
/// </summary>
public sealed class TileServiceTests
{
    private static readonly WebMercatorTileScheme Scheme = new();

    [Fact]
    public async Task A_miss_renders_one_tile_at_the_scheme_viewport_and_caches_it()
    {
        var renderer = new RecordingRenderer();
        var service = Service(renderer);
        var spec = Spec();

        var first = await service.RenderAsync(spec, "v1", new TileCoordinate(0, 0, 0), Scheme, CancellationToken.None);
        var second = await service.RenderAsync(spec, "v1", new TileCoordinate(0, 0, 0), Scheme, CancellationToken.None);

        Assert.False(first.Cached);
        Assert.True(second.Cached);
        Assert.Equal(1, renderer.Calls);
        var viewport = Assert.Single(renderer.Viewports);
        Assert.Equal(256, viewport.Width);
        Assert.Equal(256, viewport.Height);
        Assert.Equal("EPSG:3857", viewport.Crs);
        Assert.Equal(-WebMercatorTileScheme.OriginShift, viewport.Bounds.MinX, 6);
    }

    [Fact]
    public async Task A_new_version_or_format_bypasses_the_cached_tile()
    {
        var renderer = new RecordingRenderer();
        var service = Service(renderer);
        await service.RenderAsync(Spec(), "v1", new TileCoordinate(1, 0, 0), Scheme, CancellationToken.None);

        await service.RenderAsync(Spec(), "v2", new TileCoordinate(1, 0, 0), Scheme, CancellationToken.None);
        await service.RenderAsync(Spec() with { Format = RasterFormat.Jpeg }, "v1", new TileCoordinate(1, 0, 0), Scheme, CancellationToken.None);

        Assert.Equal(3, renderer.Calls);
    }

    [Fact]
    public async Task An_out_of_range_tile_is_rejected()
    {
        var service = Service(new RecordingRenderer());

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            service.RenderAsync(Spec(), "v1", new TileCoordinate(0, 1, 0), Scheme, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Resolve_falls_back_to_the_default_and_rejects_unknown_ids()
    {
        var service = Service(new RecordingRenderer());

        Assert.Same(service.DefaultScheme, service.Resolve(null));
        Assert.Same(service.DefaultScheme, service.Resolve(" "));
        Assert.Same(Scheme, service.Resolve("WEBMERCATOR"));
        Assert.Throws<SpatialException>(() => service.Resolve("mollweide"));
    }

    [Fact]
    public async Task A_batch_renders_in_request_order()
    {
        var renderer = new RecordingRenderer();
        var service = Service(renderer);
        TileCoordinate[] tiles = [new(2, 0, 0), new(2, 1, 0), new(2, 0, 1)];

        var results = await service.RenderBatchAsync(Spec(), "v1", Scheme, tiles, CancellationToken.None);

        Assert.Equal(tiles.Length, results.Count);
        for (var i = 0; i < tiles.Length; i++)
        {
            Assert.False(results[i].Cached);
            Assert.Equal(Scheme.Bounds(tiles[i]).MinX, Mark(results[i]), 6);
        }
    }

    [Fact]
    public async Task A_batch_serves_repeated_tiles_from_the_cache_after_the_first()
    {
        var renderer = new RecordingRenderer();
        var service = Service(renderer, maxTilesPerBatch: 8, concurrency: 1);
        TileCoordinate[] tiles = [new(1, 0, 0), new(1, 0, 0)];

        var results = await service.RenderBatchAsync(Spec(), "v1", Scheme, tiles, CancellationToken.None);

        Assert.False(results[0].Cached);
        Assert.True(results[1].Cached);
        Assert.Equal(1, renderer.Calls);
    }

    [Fact]
    public async Task An_empty_batch_is_rejected()
    {
        var service = Service(new RecordingRenderer());

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            service.RenderBatchAsync(Spec(), "v1", Scheme, [], CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task A_batch_above_the_configured_cap_is_rejected()
    {
        var service = Service(new RecordingRenderer(), maxTilesPerBatch: 2);

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            service.RenderBatchAsync(Spec(), "v1", Scheme, [new(3, 0, 0), new(3, 1, 0), new(3, 2, 0)], CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task A_batch_honours_cancellation()
    {
        var service = Service(new RecordingRenderer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RenderBatchAsync(Spec(), "v1", Scheme, [new(3, 0, 0)], new CancellationToken(canceled: true)));
    }

    private static TileService Service(IMapRenderer renderer, int maxTilesPerBatch = 16, int concurrency = 0) =>
        new(
            renderer,
            [Scheme],
            new InMemoryTileCache(new TileCacheOptions { MaxBytes = 1 << 20, MaxEntries = 64 }),
            new TileOptions { DefaultScheme = "webmercator", MaxTilesPerBatch = maxTilesPerBatch, Concurrency = concurrency });

    private static TileRenderSpec Spec()
    {
        var store = new WritableMemoryStore();
        return new TileRenderSpec(
            "{}",
            [new MapLayerSource("memory.places", store, store)],
            [],
            RasterFormat.Png,
            90,
            null,
            true,
            1.0);
    }

    private static double Mark(TileResult result) =>
        BitConverter.ToDouble(result.Image.Content, 0);

    /// <summary>A renderer double that records viewports and encodes the viewport minX as the image bytes.</summary>
    private sealed class RecordingRenderer : IMapRenderer
    {
        private readonly List<RasterViewport> _viewports = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<RasterViewport> Viewports
        {
            get
            {
                lock (_viewports)
                {
                    return [.. _viewports];
                }
            }
        }

        public Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            lock (_viewports)
            {
                _viewports.Add(request.Viewport);
            }

            var bytes = BitConverter.GetBytes(request.Viewport.Bounds.MinX);
            return Task.FromResult(new RasterImage(bytes, "image/png", request.Viewport.Width, request.Viewport.Height, request.Format));
        }
    }
}
