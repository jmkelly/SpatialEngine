using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>The OGC adapter's shared vector-tile orchestration keeps failure and
/// cancellation semantics while leaving MVT encoding to the implementation,
/// and keys its cache on the map plus the content versions of the datasets it
/// reads, so a write re-renders the tile (ADR-0083).</summary>
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

    private static OgcVectorTileService Service(IVectorTileService encoder, IStoreRegistry? stores = null) =>
        new(encoder, [new Scheme()], new NoCache(), stores ?? new UnversionedStores(), new OgcOptions());

    [Fact]
    public async Task A_data_write_misses_the_cache_the_next_tile_would_hit()
    {
        var cache = new RecordingCache();
        var stores = new VersionedStores("1");
        var service = new OgcVectorTileService(new RecordingEncoder(), [new Scheme()], cache, stores, new OgcOptions());
        _ = await service.RenderAsync([], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", CancellationToken.None);
        var first = cache.LastKey!;

        stores.Version = "2";
        _ = await service.RenderAsync([], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", CancellationToken.None);

        Assert.NotEqual(first, cache.LastKey);
    }

    [Fact]
    public async Task An_unchanged_dataset_keeps_the_same_key()
    {
        var cache = new RecordingCache();
        var service = new OgcVectorTileService(new RecordingEncoder(), [new Scheme()], cache, new VersionedStores("1"), new OgcOptions());
        _ = await service.RenderAsync([], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", CancellationToken.None);
        var first = cache.LastKey!;

        _ = await service.RenderAsync([], Map(), new TileCoordinate(0, 0, 0), "WebMercatorQuad", CancellationToken.None);

        Assert.Equal(first, cache.LastKey);
    }

    /// <summary>A store that reports the content version it is given, as a writable store would.</summary>
    private sealed class VersionedStores(string version) : StoreRegistryDouble
    {
        public string Version { get; set; } = version;

        protected override ValueTask<string> VersionAsync(string store, string dataset) =>
            ValueTask.FromResult(Version);
    }

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

    /// <summary>A registry over one store; the content version comes from the derived double.</summary>
    private abstract class StoreRegistryDouble : IStoreRegistry
    {
        private readonly IFeatureStore _store;

        protected StoreRegistryDouble() => _store = new VersionedStore(this);

        public IDataCatalogue Catalogue(string store) => throw new NotSupportedException();

        public IFeatureStore Features(string store) => _store;

        public IFeatureEditStore? EditStore(string store) => null;

        public IFeatureAttachmentStore? AttachmentStore(string store) => null;

        public ITransactionStore? Transactions(string store) => null;

        public IDatasetIngest? Ingest(string store) => null;

        public IRasterCatalogue? RasterCatalogue(string store) => null;

        protected abstract ValueTask<string> VersionAsync(string store, string dataset);

        private sealed class VersionedStore(StoreRegistryDouble owner) : IFeatureStore, IVersionedFeatureStore
        {
            public Task<IReadOnlyList<Spatial.Core.Features.FeatureBatch>> ScanAsync(
                string dataset, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<FeatureQueryPage> QueryAsync(
                string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<int> WriteAsync(
                string dataset, Spatial.Core.Features.FeatureBatch batch, string? transaction = null,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public ValueTask<string> GetContentVersionAsync(string dataset, CancellationToken cancellationToken = default) =>
                owner.VersionAsync("demo", dataset);
        }
    }

    /// <summary>A store that reports no content version, as every read-only provider does.</summary>
    private sealed class UnversionedStores : IStoreRegistry
    {
        public IDataCatalogue Catalogue(string store) => throw new NotSupportedException();

        public IFeatureStore Features(string store) => new UnversionedStore();

        public IFeatureEditStore? EditStore(string store) => null;

        public IFeatureAttachmentStore? AttachmentStore(string store) => null;

        public ITransactionStore? Transactions(string store) => null;

        public IDatasetIngest? Ingest(string store) => null;

        public IRasterCatalogue? RasterCatalogue(string store) => null;

        private sealed class UnversionedStore : IFeatureStore
        {
            public Task<IReadOnlyList<Spatial.Core.Features.FeatureBatch>> ScanAsync(
                string dataset, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<FeatureQueryPage> QueryAsync(
                string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<int> WriteAsync(
                string dataset, Spatial.Core.Features.FeatureBatch batch, string? transaction = null,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }

    /// <summary>A cache that records the last key it was asked for, so a test can see the key move (ADR-0083).</summary>
    private sealed class RecordingCache : ITileCache
    {
        public VectorTileCacheKey? LastKey { get; private set; }

        public ValueTask<VectorTile?> TryGetVectorAsync(VectorTileCacheKey key, CancellationToken cancellationToken = default)
        {
            LastKey = key;
            return ValueTask.FromResult<VectorTile?>(null);
        }

        public ValueTask SetVectorAsync(VectorTileCacheKey key, VectorTile tile, CancellationToken cancellationToken = default)
        {
            LastKey = key;
            return default;
        }

        public ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RasterImage?>(null);

        public ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default) => default;

        public ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => default;
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
