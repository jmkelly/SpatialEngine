using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>The OGC adapter's shared vector-tile orchestration keeps failure and
/// cancellation semantics while leaving MVT encoding to the implementation.</summary>
public sealed class OgcVectorTileServiceTests
{
    [Fact]
    public async Task A_store_failure_from_the_encoder_keeps_the_structured_code()
    {
        var service = Service(new FailingEncoder());
        var exception = await Assert.ThrowsAsync<SpatialException>(() => service.RenderAsync(
            [], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", CancellationToken.None));

        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_encoder_is_called()
    {
        var encoder = new RecordingEncoder();
        var service = Service(encoder);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(
            [], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", cancellation.Token));

        Assert.False(encoder.Called);
    }

    private static OgcVectorTileService Service(IVectorTileService encoder) =>
        new(encoder, [new Scheme()], new NoCache(), new OgcOptions());

    private static Map Map() => new(
        "world", "demo", [new MapLayer("demo.cities", 0, "cities")], [MapServiceKind.Tiles]);

    private sealed class FailingEncoder : IVectorTileService
    {
        public Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default) =>
            throw SpatialException.Unavailable("The feature store is unavailable.");
    }

    private sealed class RecordingEncoder : IVectorTileService
    {
        public bool Called { get; private set; }

        public Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new VectorTile([1]));
        }
    }

    private sealed class NoCache : ITileCache
    {
        public ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RasterImage?>(null);

        public ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default) => default;

        public ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => default;
    }

    private sealed class Scheme : ITileScheme
    {
        public string Id => "webmercator";
        public string Crs => "EPSG:3857";
        public int TileSize => 256;
        public int MinZoom => 0;
        public int MaxZoom => 23;
        public IReadOnlyList<TileLevel> Levels { get; } = [new TileLevel(0, 1, 1)];
        public bool IsValid(TileCoordinate coordinate) => coordinate.Z is >= 0 and <= 23 && coordinate.X == 0 && coordinate.Y == 0;
        public Envelope Bounds(TileCoordinate coordinate) => new(-1, -1, 1, 1);
        public double Resolution(int zoom) => 1;
    }
}
