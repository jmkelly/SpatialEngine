using Spatial.Core.Features;
using Spatial.QueryConformance;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory provider's conformance with the shared pushdown-equals-reference
/// suite (ADR-0084). The memory store has no dialect to drift from, so this is
/// the suite's own regression net: if the reference executor's semantics change,
/// this fails before a provider's pushdown is written against the old ones.
/// </summary>
public sealed class MemoryQueryConformanceTests
{
    [Fact]
    public async Task The_memory_store_matches_the_reference_over_the_conformance_fixture()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.fixture", new FeatureBatch(QueryFixture.Schema, []), 4326);
        await store.WriteAsync("memory.fixture", new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(store, "memory.fixture");
    }

    [Fact]
    public async Task The_fixture_has_the_cases_the_suite_needs()
    {
        // The suite's claims about itself, pinned: a tie in the sort key, a null
        // in the summed field, a single-row group, a null group key, and a row
        // outside the box.
        var scores = QueryFixture.Features.Select(feature => feature["score"]).ToArray();
        Assert.Contains(scores, score => score.IsNull);
        Assert.Equal(2, scores.Count(score => !score.IsNull && score.Int64Value == 20));
        Assert.Equal(1, QueryFixture.Features.Count(feature => !feature["category"].IsNull && feature["category"].StringValue == "c"));
        Assert.Contains(QueryFixture.Features, feature => feature["category"].IsNull);
        Assert.Single(QueryFixture.Features, feature => feature["shape"].GeometryValue.Envelope!.Value.MinX > QueryFixture.Box.MaxX);
    }
}
