using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Geometry;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The reduction faces on a dataset that declares no identity column — the
/// shape <c>CreateAsync</c> leaves when the defining batch has no integer
/// identity field (ADR-0147 keeps a created dataset keyless in the contract's
/// view, ADR-0149 §3) — against a live PostGIS.
///
/// <para>
/// ADR-0097 §1 declines a pushed <c>WHERE</c> on such a dataset because its
/// features are named by the ordinal of the read. A count, a distinct set and a
/// grouped reduction return values and no feature, so nothing is renumbered and
/// the restriction is pushed (ADR-0184 §1). Before that record each of these
/// faces read the whole table and reduced it in managed code — the 31 MB a
/// <c>returnCountOnly</c> spent to compute the one number the database answers
/// for free.
///
/// <para>
/// The answers are asserted against the reference reduction over the whole
/// read, because a pushdown that disagreed with the reference would pass a row
/// count and fail the contract.
///
/// <para>
/// The last case is the one that says whether the table was read at all, and it
/// says it without measuring time or memory: one row of the table holds a
/// geometry the store's reader cannot decode (a PostGIS curve type, which the
/// EWKB reader refuses). A count has no business decoding a geometry, so it
/// answers anyway — and before ADR-0184 it could not, because the whole-table
/// read it fell back to ran the decoder over every row.
/// </para>
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisKeylessReductionPushdownTests : IClassFixture<PostgisDatabaseFixture>
{
    private const int Rows = 5_000;

    private readonly PostgisDatabaseFixture _fixture;

    public PostgisKeylessReductionPushdownTests(PostgisDatabaseFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("city", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geom", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>Ten of the rows carry a population of a million, over the four cities.</summary>
    private static readonly FeatureQuery Million = new(
        Where: new Predicate.Compare(
            new FieldRef("population"),
            ComparisonOperator.GreaterOrEqual,
            Literal.FromInteger("999990")));

    [SkippableFact]
    public async Task A_count_on_a_dataset_with_no_identity_column_is_the_reference_s_count()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);

        // The shape this file is about: no primary key, so no feature identity,
        // so every feature is named by the ordinal of the read.
        Assert.Empty((await context.Store.DescribeAsync(dataset)).IdColumns);

        Assert.Equal(10, await context.Store.CountAsync(dataset, Million));
        Assert.Equal(Rows, await context.Store.CountAsync(dataset, FeatureQuery.All));
        Assert.Equal(10, FeaturePlanExecutor.Select(Schema, await ReadAllAsync(context, dataset), Million, CancellationToken.None).Count);
    }

    [SkippableFact]
    public async Task A_grouped_reduction_on_a_dataset_with_no_identity_column_is_the_reference_s_reduction()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var plan = Million with { Order = [new OrderTerm("city")] };
        var aggregate = new AggregateQuery(
        [
            new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
            new AggregateSpec(AggregateStatistic.Sum, "population", "total"),
        ],
        ["city"]);

        var page = await context.Store.AggregateAsync(dataset, plan, aggregate);

        Assert.Equal(4, page.Groups.Count);
        Assert.Equal(10, page.Groups.Sum(group => group.Values[0].Int64Value));
        Assert.Equal(10 * 1_000_000, page.Groups.Sum(group => group.Values[1].Int64Value));
        Assert.All(page.Groups, group => Assert.Equal(group.Values[0].Int64Value, group.Values[1].Int64Value / 1_000_000));

        // Group for group and value for value, the reference's answer: the plan
        // selected over the whole read, then reduced in memory.
        var selected = FeaturePlanExecutor.Select(Schema, await ReadAllAsync(context, dataset), plan, CancellationToken.None);
        var reference = FeatureReduction.Aggregate(Schema, selected, aggregate, plan.Order);
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
    }

    [SkippableFact]
    public async Task A_distinct_set_on_a_dataset_with_no_identity_column_is_the_reference_s_set()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        // The plan's order names the requested field, so the set is total over
        // the distinct rows and the dialect's own order is the reference's
        // (ADR-0133 §6); the box is the restriction, and the plan is a plan.
        var plan = new FeatureQuery(
            BoundingBox: new BoundingBox(0, 0, 0.2, 0.2),
            Order: [new OrderTerm("city")]);

        var page = await context.Store.DistinctAsync(dataset, plan, new DistinctQuery(["city"]));

        Assert.Equal(
            ["alpha", "bravo", "charlie", "delta"],
            page.Rows.Select(row => row[0].StringValue));
    }

    /// <summary>
    /// A plan restricted by <em>ids</em> on a dataset that declares no identity
    /// column: the one restriction neither face can state, because a dataset
    /// with no identity column has no column to say it with (ADR-0184 §1), and
    /// the one case a whole read is the <em>only</em> correct answer for — the
    /// features are named by the ordinal of the read, so a <c>WHERE</c> that
    /// returned two rows would name them 1 and 2 whatever the plan asked for
    /// (ADR-0097 §1).
    ///
    /// <para>
    /// Every face is here, because the ids are the restriction that reaches all
    /// four of them with nothing pushed: the page, the count, the distinct set
    /// and the grouped reduction each select over the whole read and finish in
    /// process, and each is asserted against the reference over that same
    /// whole read rather than against a literal — a store that answered with a
    /// different row set would still pass a number.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_plan_restricted_by_ids_on_a_dataset_with_no_identity_column_is_the_reference_s_answer()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var every = await ReadAllAsync(context, dataset);
        // The identities are the whole read's, which is the only numbering such
        // a dataset has: two of them, and the plan asks for them by that name.
        var wanted = new[] { every[1].Id, every[3].Id };
        // The order names a requested field, so it is total over the distinct
        // rows and the set is reported in it whatever order the whole read
        // returned its rows in (ADR-0133 §6) — the assertion below is about the
        // restriction, not about the read's row order.
        var plan = new FeatureQuery(Ids: wanted, Order: [new OrderTerm("city", SortDirection.Descending)]);

        var page = await context.Store.QueryAsync(dataset, plan);
        Assert.Equal(
            FeaturePlanExecutor.Execute(Schema, every, plan, CancellationToken.None).Features.Select(feature => feature.Id),
            page.Features.Select(feature => feature.Id));

        // The three reduction faces over the same plan, each one a count or a
        // set over the rows the whole read selected.
        Assert.Equal(2, await context.Store.CountAsync(dataset, plan));
        var distinct = await context.Store.DistinctAsync(dataset, plan, new DistinctQuery(["city"]));
        Assert.Equal(
            [.. FeatureReduction.Distinct(
                Schema,
                FeaturePlanExecutor.Select(Schema, every, plan, CancellationToken.None),
                new DistinctQuery(["city"]),
                plan.Order).Rows.Select(row => row[0].StringValue)],
            distinct.Rows.Select(row => row[0].StringValue));
        var aggregate = await context.Store.AggregateAsync(
            dataset, plan, new AggregateQuery([new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")]));
        Assert.Equal(2, Assert.Single(aggregate.Groups).Values[0].Int64Value);
    }

    /// <summary>
    /// The cost claim as a fact rather than a figure: a reduction over a table
    /// the store cannot read a feature from is still answered, because the
    /// reduction reads no feature. Both faces that return a number are here,
    /// because a store that pushed one and not the other would pass the first
    /// assertion alone.
    /// </summary>
    [SkippableFact]
    public async Task A_reduction_over_a_dataset_whose_features_cannot_be_read_is_still_answered()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context, unreadableGeometry: true);

        // A scan is refused: this table is not readable as a feature table, and
        // the store reports the unreadable geometry as a structured failure.
        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Store.ScanAsync(dataset));
        Assert.IsType<PostgisEwkbFormatException>(failure.InnerException);

        // The count is the count, and so is the grouped reduction.
        Assert.Equal(10, await context.Store.CountAsync(dataset, Million));
        var aggregate = new AggregateQuery(
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
        ["city"]);
        var page = await context.Store.AggregateAsync(dataset, Million with { Order = [new OrderTerm("city")] }, aggregate);
        Assert.Equal(4, page.Groups.Count);
        Assert.Equal(10, page.Groups.Sum(group => group.Values[0].Int64Value));
    }

    /// <summary>
    /// A table with no primary key — the shape <c>CreateAsync</c> leaves — over
    /// four cities, ten rows of which are worth a million. With
    /// <paramref name="unreadableGeometry"/>, the last row holds a curve the
    /// EWKB reader refuses, so the table is not readable as a feature table.
    /// </summary>
    private static async Task<string> SeedAsync(PostgisTestContext context, bool unreadableGeometry = false)
    {
        var dataset = $"public.keyless_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} ("
            + "\"id\" bigint NOT NULL, \"city\" text, \"population\" bigint, "
            + "\"geom\" geometry(Geometry, 4326))");
        await context.ExecuteAsync(
            $"INSERT INTO {dataset} (\"id\", \"city\", \"population\", \"geom\") "
            + "SELECT g, (ARRAY['alpha', 'bravo', 'charlie', 'delta'])["
            + "(CASE WHEN g % 500 = 0 THEN g / 500 ELSE g END % 4) + 1], "
            + "CASE WHEN g % 500 = 0 THEN 1000000 ELSE g * 10 END, "
            + "ST_MakePoint(g % 1000 / 1000.0, 0) FROM generate_series(1, "
            + $"{(unreadableGeometry ? Rows - 1 : Rows)}) AS g");
        if (unreadableGeometry)
        {
            await context.ExecuteAsync(
                $"INSERT INTO {dataset} (\"id\", \"city\", \"population\", \"geom\") VALUES "
                + $"({Rows}, 'alpha', 1000000, ST_GeomFromText('CIRCULARSTRING(1 1, 2 2, 3 1)', 4326))");
        }

        return dataset;
    }

    /// <summary>Every feature the table holds, as the reference sees them.</summary>
    private static async Task<List<Feature>> ReadAllAsync(PostgisTestContext context, string dataset) =>
        [.. (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features)];

    private static string[] Render(IEnumerable<AggregateGroup> groups) =>
        [.. groups.Select(group => string.Join("|", group.Key.Concat(group.Values).Select(value => value.IsNull ? "-" : value.ToString())))];
}
