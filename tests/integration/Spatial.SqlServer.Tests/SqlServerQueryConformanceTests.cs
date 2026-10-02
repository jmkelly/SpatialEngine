using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.QueryConformance;
using Spatial.Querying;
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
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerQueryConformanceTests : IClassFixture<SqlServerDatabaseFixture>
{
    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerQueryConformanceTests(SqlServerDatabaseFixture fixture) => _fixture = fixture;

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
    /// Every statistic T-SQL has a plain aggregate for, reduced by the server
    /// over the plan's group order, compared with the reference over the same
    /// rows (ADR-0133 §3). These are the values whose T-SQL spelling is not the
    /// field's own type — a sum is a <c>bigint</c>, a mean is a division
    /// because <c>AVG</c> over an integer column is integer division, a variance
    /// is the sample form — so a store that pushed them down without stating
    /// that returns a different number here, and one that never pushed them
    /// passes it for the wrong reason.
    /// </summary>
    [SkippableFact]
    public async Task The_pushed_statistics_are_the_reference_statistics()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.stats_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id int NOT NULL PRIMARY KEY, category nvarchar(100) NULL, "
            + "score int NULL, ratio float NULL, name nvarchar(200) NULL, shape geometry NULL)");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var aggregate = new AggregateQuery(
            [
                new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
                new AggregateSpec(AggregateStatistic.Count, "score", "scored"),
                new AggregateSpec(AggregateStatistic.Sum, "score", "total"),
                new AggregateSpec(AggregateStatistic.Average, "score", "mean"),
                new AggregateSpec(AggregateStatistic.Minimum, "score", "lo"),
                new AggregateSpec(AggregateStatistic.Maximum, "score", "hi"),
                new AggregateSpec(AggregateStatistic.Minimum, "name", "first"),
                new AggregateSpec(AggregateStatistic.Maximum, "name", "last"),
                new AggregateSpec(AggregateStatistic.Variance, "ratio", "var"),
                new AggregateSpec(AggregateStatistic.StdDev, "ratio", "sd"),
            ],
            ["category"]);

        var query = new FeatureQuery(Where: new Predicate.Compare(
            new FieldRef("score"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("0")),
            Order: [new OrderTerm("category")]);

        var scan = await context.Store.ScanAsync(dataset);
        var schema = (FeatureSchema)scan[0].Schema;
        var expected = FeatureReduction.Aggregate(
            schema, FeaturePlanExecutor.Select(schema, scan.SelectMany(batch => batch.Features).ToList(), query, default), aggregate, query.Order);
        var actual = await context.Store.AggregateAsync(dataset, query, aggregate);

        Assert.Equal(expected.GroupFields, actual.GroupFields);
        Assert.Equal(expected.ValueNames, actual.ValueNames);
        Assert.Equal(Rows(expected.Groups), Rows(actual.Groups));
    }

    /// <summary>
    /// An ungrouped reduction that selects nothing is <em>one group of nulls</em>
    /// (ADR-0098 §3), which SQL does not say: an ungrouped aggregate always
    /// returns a row, and its <c>COUNT(*)</c> of that row is a zero where the
    /// contract's answer is a null (ADR-0133 §4).
    /// </summary>
    [SkippableFact]
    public async Task An_ungrouped_reduction_of_nothing_is_one_group_of_nulls()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.empty_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id int NOT NULL PRIMARY KEY, category nvarchar(100) NULL, "
            + "score int NULL, ratio float NULL, name nvarchar(200) NULL, shape geometry NULL)");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var aggregate = new AggregateQuery(
            [
                new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
                new AggregateSpec(AggregateStatistic.Count, "score", "scored"),
                new AggregateSpec(AggregateStatistic.Sum, "score", "total"),
                new AggregateSpec(AggregateStatistic.Average, "ratio", "mean"),
            ]);
        var query = new FeatureQuery(Where: new Predicate.Compare(
            new FieldRef("score"), ComparisonOperator.GreaterThan, Literal.FromNumber(double.MaxValue)));

        var page = await context.Store.AggregateAsync(dataset, query, aggregate);

        var group = Assert.Single(page.Groups);
        Assert.Equal(AttributeValue.FromInt64(0), group.Values[0]);
        Assert.Equal(AttributeValue.Null, group.Values[1]);
        Assert.Equal(AttributeValue.Null, group.Values[2]);
        Assert.Equal(AttributeValue.Null, group.Values[3]);
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

    /// <summary>
    /// The groups as text, for the whole-answer comparison: a
    /// <em>double</em> reduction is rendered to twelve significant digits,
    /// because a pushed-down statistic is a decimal engine and a binary one
    /// agreeing to the precision a JSON client can tell. Everything else — a
    /// count, a sum, an extreme, a null, a key — is compared exactly, because
    /// those have no such slack.
    /// </summary>
    private static string[] Rows(IEnumerable<AggregateGroup> groups) =>
        [.. groups.Select(group => string.Join(
            "|",
            group.Key.Concat(group.Values).Select(value => value.Kind == AttributeKind.Double
                ? value.DoubleValue.ToString("G12", System.Globalization.CultureInfo.InvariantCulture)
                : value.IsNull ? "-" : value.ToString())))];
}
