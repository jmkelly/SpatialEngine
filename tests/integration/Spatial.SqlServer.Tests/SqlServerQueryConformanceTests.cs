using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.QueryConformance;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The SQL Server provider against the shared pushdown-equals-reference suite
/// (ADR-0098): the restriction is pushed into T-SQL and the plan read, the
/// count, the distinct set and the grouped aggregate are finished by the
/// shared reference executor, so every one of them must answer exactly what
/// that reference answers over the same fixture — including the rows a
/// dialect is tempted to get wrong (ties, nulls, single-row groups, the empty
/// set) and the plans that carry a predicate. Skips with an explicit reason
/// without Docker.
/// </summary>
public sealed class SqlServerQueryConformanceTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerQueryConformanceTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_pushed_down_answers_match_the_reference_over_the_conformance_fixture()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.fixture_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(context.Store, dataset);
    }

    [SkippableFact]
    public async Task A_restricted_read_keeps_the_identity_the_whole_read_gave_each_row()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.identity_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        // The dataset has no identity column, so a feature's id is its ordinal
        // in the read. A restriction that reached SQL would renumber the rows
        // that survived it, and the same feature would come back with an id
        // that depends on the query (ADR-0097) — so the store keeps the
        // restriction and the ids are the full read's.
        var all = await context.Store.ScanAsync(dataset);
        var restricted = await context.Store.QueryAsync(dataset, new FeatureQuery(Where: FeatureFilter.Parse("score >= 0")));

        var expected = all.SelectMany(batch => batch.Features)
            .Where(feature => !feature["score"].IsNull)
            .Select(feature => feature.Id.Value)
            .ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, restricted.Features.Select(feature => feature.Id.Value).ToArray());
    }

    /// <summary>
    /// The same suite over a table that <em>has</em> an identity, which is the
    /// one shape where this store pushes the order and the page rather than
    /// finishing the plan in process (ADR-0124 §2). The fixture is built for
    /// exactly this: two rows tie on the sort key, so the page boundary can
    /// only be cut in one place if the pushed tie-break is the reference's, and
    /// the text columns order differently under the container's collation than
    /// under the contract's.
    /// </summary>
    [SkippableFact]
    public async Task The_pushed_page_matches_the_reference_over_an_identity_carrying_table()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.keyed_{Guid.NewGuid().ToString("N")[..8]}";
        // `CreateAsync` creates a table without a primary key, so the table is
        // made by hand — a key is the one thing a pushed page needs and the
        // sample-based create does not add.
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id int NOT NULL PRIMARY KEY, category nvarchar(100) NULL, "
            + "score int NULL, ratio float NULL, name nvarchar(200) NULL, shape geometry NULL)");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(context.Store, dataset);
    }

    /// <summary>
    /// A grouped reduction under a plan ordered by the group key answers the
    /// <em>plan's</em> group order, both directions, and the page cuts that
    /// order (ADR-0128 §8). This store reduces in managed code, so the order is
    /// the reference's, not T-SQL's — and the two disagree here on purpose: the
    /// container's default collation is a case-insensitive one, which puts
    /// <c>"a"</c> before <c>"A"</c> and the byte-order comparison the contract
    /// states does not, and T-SQL sorts a null lowest there and last here. A
    /// store that answered with the order the rows came back in would be
    /// answering a different question from the one every other store is
    /// measured by.
    /// </summary>
    [SkippableFact]
    public async Task A_grouped_reduction_applies_the_plan_order_to_a_case_varying_key()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.grouporder_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id int NOT NULL PRIMARY KEY, category nvarchar(100) NULL, "
            + "score int NULL, ratio float NULL, name nvarchar(200) NULL, shape geometry NULL)");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var grouped = new AggregateQuery(
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            ["category"]);

        foreach (var (direction, expected) in Orders())
        {
            var page = await context.Store.AggregateAsync(
                dataset, new FeatureQuery(Order: [new OrderTerm("category", direction)]), grouped);

            Assert.Equal(expected, Keys(page));
        }

        // The cap cuts the ordered groups, so a page is a window of the same
        // sequence and not a page of the order the rows arrived in (ADR-0128 §3).
        var windowed = await context.Store.AggregateAsync(
            dataset,
            new FeatureQuery(Order: [new OrderTerm("category")]),
            grouped with { Limit = 2, Offset = 1 });

        Assert.Equal([Key("_c"), Key("a")], Keys(windowed));
        Assert.True(windowed.HasMore);
    }

    /// <summary>
    /// The group order the plan asks for over a text key, with the null last
    /// ascending and first descending, and every string compared by its bytes
    /// (ADR-0098 §3, ADR-0121): <c>"A"</c> and <c>"_c"</c> are below
    /// <c>"a"</c>, and none of them is where the container's locale collation
    /// puts it.
    /// </summary>
    private static IEnumerable<(SortDirection Direction, AttributeValue[] Keys)> Orders() =>
    [
        (SortDirection.Ascending, [Key("A"), Key("_c"), Key("a"), AttributeValue.Null]),
        (SortDirection.Descending, [AttributeValue.Null, Key("a"), Key("_c"), Key("A")]),
    ];

    private static AttributeValue Key(string category) => AttributeValue.FromString(category);

    private static AttributeValue[] Keys(AggregatePage page) =>
        [.. page.Groups.Select(group => group.Key[0])];
}
