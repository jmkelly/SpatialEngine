using BenchmarkDotNet.Attributes;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.Provider.Memory;

namespace Spatial.Performance;

/// <summary>
/// Feature query micros (T-076): the store verbs <c>FeatureQueryEngine</c>
/// fans out to, over a 2,000-point <c>MemoryStore</c> dataset. The engine
/// itself is internal to <c>Spatial.Adapter.GeoServices</c> (covered by its
/// own unit suite), so these benches pin the cost of the scan and the bbox
/// match underneath it: full scan vs a selective bbox.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FeatureQueryBenchmarks
{
    private const string Dataset = "bench.places";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private MemoryStore _store = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _store = new MemoryStore();
        await _store.CreateAsync(Dataset, new FeatureBatch(Schema, []), srid: 4326);

        const int total = 2_000;
        const int batch = 500;
        for (var offset = 0; offset < total; offset += batch)
        {
            var features = new List<Feature>(batch);
            for (var i = offset; i < offset + batch; i++)
            {
                var x = (i % 100) * 0.1;
                var y = (i / 100) * 0.1;
                features.Add(new Feature(
                    new FeatureId($"place-{i}"),
                    Schema,
                    [
                        AttributeValue.FromString($"Place {i}"),
                        AttributeValue.FromInt64(i),
                        AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
                    ]));
            }

            await _store.WriteAsync(Dataset, new FeatureBatch(Schema, features));
        }
    }

    [Benchmark(Description = "MemoryStore scan (2k points)")]
    public async Task<int> Scan_All()
    {
        var batches = await _store.ScanAsync(Dataset);
        return batches.Sum(batch => batch.Count);
    }

    [Benchmark(Description = "MemoryStore bbox query (2k points, ~1% match)", Baseline = true)]
    public async Task<int> Query_Bbox()
    {
        var batches = await _store.QueryAsync(Dataset, new BoundingBox(0, 0, 1, 0.2));
        return batches.Sum(batch => batch.Count);
    }
}
