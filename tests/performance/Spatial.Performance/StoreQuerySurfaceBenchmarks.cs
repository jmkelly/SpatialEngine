using BenchmarkDotNet.Attributes;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;
using Spatial.Stores.Memory;

namespace Spatial.Performance;

/// <summary>
/// The served reductions over a 34k-row layer, the layer size
/// SpatialEngine-u2x.1 spiked (ADR-0098). Each bench is one of the shapes the
/// GeoServices adapter used to compute over a materialised match set — the
/// count, the grouped statistics, the paged ordered read — against the same
/// layer read whole.
///
/// <para>
/// The point is not the absolute numbers but the <em>ratio</em>: a store that
/// can push a plan down answers these from a bounded amount of work, and a
/// store that cannot is bounded by the scan. The <c>reference</c> benches are
/// the same three shapes through the shared executor over every row, which is
/// the cost the pushdown removes — and the cost a store without a pushdown
/// still pays.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class StoreQuerySurfaceBenchmarks
{
    private const string Dataset = "bench.cities";
    private const int Total = 34_000;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("category", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private MemoryStore _store = null!;
    private Feature[] _rows = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _rows = Rows();
        _store = new MemoryStore();
        await _store.CreateAsync(Dataset, new FeatureBatch(Schema, []), srid: 4326);
        const int batch = 2_000;
        for (var offset = 0; offset < Total; offset += batch)
        {
            await _store.WriteAsync(
                Dataset, new FeatureBatch(Schema, _rows[offset..Math.Min(offset + batch, Total)]));
        }
    }

    private static Feature[] Rows()
    {
        var rows = new Feature[Total];
        for (var i = 0; i < Total; i++)
        {
            var x = (i % 200) * 0.05;
            var y = (i / 200) * 0.05;
            rows[i] = new Feature(
                new FeatureId($"city-{i}"),
                Schema,
                [
                    AttributeValue.FromString($"City {i}"),
                    AttributeValue.FromString($"category-{i % 12}"),
                    AttributeValue.FromInt64(1_000 + (i % 977)),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
                ]);
        }

        return rows;
    }

    private static AggregateQuery Statistics => new(
    [
        new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
        new AggregateSpec(AggregateStatistic.Sum, "population", "total"),
        new AggregateSpec(AggregateStatistic.Average, "population", "mean"),
    ],
    ["category"]);

    [Benchmark(Description = "scan 34k rows (the baseline the reductions replace)", Baseline = true)]
    public async Task<int> Scan_All()
    {
        var batches = await _store.ScanAsync(Dataset);
        return batches.Sum(batch => batch.Count);
    }

    [Benchmark(Description = "plan read: 20 of 34k rows, ordered and projected")]
    public async Task<int> Read_First_Twenty()
    {
        var page = await _store.QueryAsync(
            Dataset,
            new FeatureQuery(Projection: ["name", "population"], Order: [new OrderTerm("population", SortDirection.Descending)], Limit: 20));
        return page.Features.Count();
    }

    [Benchmark(Description = "count over 34k rows (returnCountOnly)")]
    public Task<int> Count_All() => FeatureReductionFallback.CountAsync(_store, Dataset, FeatureQuery.All);

    [Benchmark(Description = "grouped statistics over 34k rows (outStatistics)")]
    public Task<int> Aggregate_All() => FeatureReductionFallback
        .AggregateAsync(_store, Dataset, FeatureQuery.All with { Order = [new OrderTerm("category")] }, Statistics)
        .ContinueWith(task => task.Result.Groups.Count, TaskContinuationOptions.ExecuteSynchronously);

    [Benchmark(Description = "reference: count over all 34k rows in memory")]
    public int Reference_Count_All() => FeatureReduction.CountFeatures(_rows);
}
