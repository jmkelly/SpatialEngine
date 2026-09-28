using Spatial.Core.Features;
using Spatial.PredicateConformance;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory store against the shared conformance fixture
/// (ADR-0074 §4): the same cases the PostGIS and SQL Server integration
/// suites run, so the reference evaluator and both SQL pushdowns are held to
/// one answer. In-process and docker-free, so it runs everywhere.
/// </summary>
public sealed class MemoryPredicateConformanceTests
{
    [Fact]
    public async Task The_memory_store_answers_every_conformance_case()
    {
        var store = new MemoryStore();
        await store.CreateAsync(PredicateConformanceSuite.Dataset, PredicateConformanceSuite.Sample, PredicateConformanceSuite.Srid);
        await store.WriteAsync(PredicateConformanceSuite.Dataset, new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(store, PredicateConformanceSuite.Dataset);

        Assert.Empty(failures);
    }
}
