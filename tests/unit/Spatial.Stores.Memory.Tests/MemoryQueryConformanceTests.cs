using Spatial.Core.Features;
using Spatial.QueryConformance;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory provider's conformance with the shared pushdown-equals-reference
/// suite (ADR-0098). The memory store has no dialect to drift from, so this is
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
        Assert.Equal(1, QueryFixture.Features.Count(feature => !feature["category"].IsNull && feature["category"].StringValue == "_c"));
        Assert.Contains(QueryFixture.Features, feature => feature["category"].IsNull);
        Assert.Single(QueryFixture.Features, feature => feature["shape"].GeometryValue.Envelope!.Value.MinX > QueryFixture.Box.MaxX);
    }

    [Fact]
    public void The_fixture_orders_its_text_keys_by_bytes_and_not_by_a_locale_collation()
    {
        // The suite's text-order cases are only worth running if the fixture's
        // text columns make the two rules disagree (ADR-0121). These two pins
        // are the property: the reference's ordinal sequence is stated, and the
        // case-insensitive sequence a locale collation approximates is asserted
        // to be a different one — so a pushed-down text sort key that inherits
        // the database's collation answers a different question from the
        // reference, and the suite catches it.
        var ordinal = QueryFixture.Features
            .Select(feature => feature["name"].StringValue)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Alpha", "Charlie", "_charlie", "a-delta", "bravo", "echo"], ordinal);
        Assert.NotEqual(
            ordinal,
            QueryFixture.Features
                .Select(feature => feature["name"].StringValue)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        // The group key, which is what the grouped reductions order by: "A" and
        // "_c" before "a" by bytes, and a different sequence again under a
        // comparison that folds case — so a `GROUP BY ... ORDER BY` pushed into
        // SQL without saying how it compares strings answers differently (the
        // null group is pinned above, and the suite compares its placement).
        var groups = QueryFixture.Features
            .Where(feature => !feature["category"].IsNull)
            .Select(feature => feature["category"].StringValue)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["A", "A", "_c", "a", "a"], groups);
        Assert.NotEqual(
            groups,
            QueryFixture.Features
                .Where(feature => !feature["category"].IsNull)
                .Select(feature => feature["category"].StringValue)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
