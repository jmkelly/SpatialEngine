using BenchmarkDotNet.Attributes;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Rendering.Skia;

namespace Spatial.Performance;

/// <summary>
/// Tile render micros (T-076): a 256×256 PNG tile through the real
/// <c>MapRenderer</c> (scene build with <c>NtsGeometryOperations</c>, Skia
/// rasterize, PNG encode, vector-only path). The empty-tile bench pins the
/// fixed pipeline overhead; the 200-point bench adds the per-feature cost,
/// so the delta is the feature path.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TileRenderBenchmarks
{
    private const string Dataset = "bench.cities";
    private const string Style = """
        { "version": 8, "layers": [
            { "id": "cities", "type": "circle", "source-layer": "bench.cities",
              "paint": { "circle-color": "#ffd166", "circle-radius": 4 } } ] }
        """;

    private MapRenderer _renderer = null!;
    private MapRenderRequest _empty = null!;
    private MapRenderRequest _points = null!;

    [GlobalSetup]
    public void Setup()
    {
        _renderer = new MapRenderer(new IdentityTransforms(), new NtsGeometryOperations());
        var viewport = new RasterViewport(new Envelope(-10, -10, 10, 10), 256, 256, "EPSG:4326");
        _empty = new MapRenderRequest(
            viewport, Style, [new MapLayerSource(Dataset, new BenchStore([]), new BenchCatalogue())]);
        _points = new MapRenderRequest(
            viewport, Style, [new MapLayerSource(Dataset, new BenchStore(Grid(200)), new BenchCatalogue())]);
    }

    [Benchmark(Description = "Skia tile render 256px (empty layer)", Baseline = true)]
    public async Task<int> Render_EmptyTile() => (await _renderer.RenderAsync(_empty)).Content.Length;

    [Benchmark(Description = "Skia tile render 256px (200 points)")]
    public async Task<int> Render_PointsTile() => (await _renderer.RenderAsync(_points)).Content.Length;

    private static Feature[] Grid(int count)
    {
        var schema = BenchSchema.Value;
        var features = new Feature[count];
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        for (var i = 0; i < count; i++)
        {
            var x = -9.0 + (18.0 * (i % side) / side);
            var y = -9.0 + (18.0 * (i / side) / side);
            features[i] = new Feature(
                new FeatureId($"city-{i}"),
                schema,
                [
                    AttributeValue.FromString($"City {i}"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
                ]);
        }

        return features;
    }

    private static class BenchSchema
    {
        public static readonly FeatureSchema Value = new(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
    }

    private sealed class IdentityTransforms : ICoordinateTransforms
    {
        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) =>
            geometry;
    }

    private sealed class BenchStore(Feature[] features) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
            string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(BenchSchema.Value, features)];
            return Task.FromResult(batches);
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BenchCatalogue : IDataCatalogue
    {
        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatasetDescription(
                dataset, "public", dataset, "geometry", 4326, "Point", 1, ["id"], BenchSchema.Value));

        public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
