using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The <c>outStatistics</c> reduction against a live PostGIS (ADR-0098 §3 and
/// §7, SpatialEngine-u2x.13): the request's statistics and its grouping are one
/// <c>GROUP BY</c> — or one aggregate row, ungrouped — and the answer is the
/// answer the reference reduction gives over the same table.
///
/// <para>
/// Two assertions per case, and the second is the one that matters. The call
/// count says the table was not read to be reduced afterwards in managed code;
/// the value comparison says the numbers and the rows that came back are the
/// reference's. The cases are the ones a dialect is tempted to get wrong, and
/// each is a rule rather than a fixture: a group that exists with one member
/// (the sample variance is undefined, so it is null and not zero), a group that
/// does not exist (no row at all — a <c>LEFT JOIN</c> would answer one), a
/// column with nulls in it (a count skips them, and a count of nothing is null),
/// a group whose values interpolate (a percentile between two rows), and no
/// rows at all (one row of nulls ungrouped, no rows grouped).
/// </para>
/// </summary>
public sealed class PostgisStatisticsPushdownIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisStatisticsPushdownIntegrationTests(PostgisContainerFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("city", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geom", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>
    /// Five rows over four groups: one group with two members whose populations
    /// differ (so a percentile interpolates between them), one with a null
    /// population (a count skips it, and the group of nothing left is null), and
    /// two single-member groups (which the sample variance cannot describe),
    /// plus a null-keyed group of its own.
    /// </summary>
    private static readonly IReadOnlyList<Feature> Rows =
    [
        Row(1, "alpha", 100, 1),
        Row(2, "alpha", 200, 2),
        Row(3, "bravo", null, 3),
        Row(4, "charlie", 400, 4),
        Row(5, null, 300, 5),
    ];

    private static AggregateSpec[] Specs =>
    [
        new(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
        new(AggregateStatistic.Count, "population", "scored"),
        new(AggregateStatistic.Sum, "population", "total"),
        new(AggregateStatistic.Average, "population", "mean"),
        new(AggregateStatistic.Minimum, "city", "first"),
        new(AggregateStatistic.Maximum, "city", "last"),
        new(AggregateStatistic.Variance, "population", "var"),
        new(AggregateStatistic.StdDev, "population", "sd"),
        new(AggregateStatistic.PercentileContinuous, "population", "p90", 0.9),
        new(AggregateStatistic.PercentileDiscrete, "population", "p50", 0.5),
        new(AggregateStatistic.Envelope, "geom", "box"),
    ];

    [SkippableFact]
    public async Task An_ungrouped_reduction_is_one_aggregate_row_and_agrees_with_the_reference()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var page = await counting.AggregateAsync(dataset, FeatureQuery.All, new AggregateQuery(Specs));

        // One aggregate row, and no table read behind it.
        Assert.Equal(0, counting.Scans);
        Assert.Equal(1, counting.Aggregates);

        var values = Assert.Single(page.Groups).Values;
        Assert.Equal(5, values[0].Int64Value);      // five rows
        Assert.Equal(4, values[1].Int64Value);      // four non-null populations
        Assert.Equal(1000, values[2].Int64Value);   // the sum of the non-null ones
        Assert.Equal(250, values[3].DoubleValue, 9);
        Assert.Equal("alpha", values[4].StringValue);
        Assert.Equal("charlie", values[5].StringValue);

        // And it is the reference's answer, group for group, value for value.
        var reference = await ReferenceAsync(context.Store, dataset, FeatureQuery.All, new AggregateQuery(Specs));
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
    }

    [SkippableFact]
    public async Task A_grouped_reduction_is_a_group_by_and_agrees_with_the_reference_in_group_key_order()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var aggregate = new AggregateQuery(Specs, ["city"]);
        var plan = new FeatureQuery(Order: [new OrderTerm("city")]);
        var page = await counting.AggregateAsync(dataset, plan, aggregate);

        Assert.Equal(0, counting.Scans);
        Assert.Equal(1, counting.Aggregates);

        var reference = await ReferenceAsync(context.Store, dataset, plan, aggregate);
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
        Assert.Equal(["alpha", "bravo", "charlie", "-"], page.Groups.Select(group => group.Key[0].IsNull ? "-" : group.Key[0].StringValue));

        // The single-member groups have no sample variance: null, not zero.
        var single = page.Groups.Where(group => group.Values[0].Int64Value == 1).ToArray();
        Assert.Equal(3, single.Length);
        Assert.All(single, group => Assert.Equal(AttributeValue.Null, group.Values[6]));

        // The group of two interpolates between its rows: 100 and 200 at 0.9
        // is 190, and the discrete rank ceil(0.5 × 2) is the first of them.
        var alpha = page.Groups[0];
        Assert.Equal(190.0, alpha.Values[8].DoubleValue, 9);
        Assert.Equal(100.0, alpha.Values[9].DoubleValue, 9);
    }

    [SkippableFact]
    public async Task A_restriction_reaches_the_aggregate_and_a_group_that_does_not_exist_is_never_a_row()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var aggregate = new AggregateQuery(Specs, ["city"]);
        var plan = new FeatureQuery(
            Where: new Predicate.Compare(
                new FieldRef("population"),
                ComparisonOperator.GreaterOrEqual,
                Literal.FromInteger("150")),
            Order: [new OrderTerm("city")]);
        var page = await counting.AggregateAsync(dataset, plan, aggregate);

        Assert.Equal(0, counting.Scans);

        // Only the groups that have a matching member: "alpha" (200),
        // "charlie" (400) and the null-keyed group (300). "bravo" is a group
        // that exists in the table with nothing in this selection, and a LEFT
        // JOIN would answer a row for it — Esri emits no row for a group with
        // no matching features, which is what a GROUP BY does.
        Assert.Equal(["alpha", "charlie", null], page.Groups.Select(group => group.Key[0].IsNull ? null : group.Key[0].StringValue));
        Assert.DoesNotContain("bravo", page.Groups.Select(group => group.Key[0].IsNull ? string.Empty : group.Key[0].StringValue));
        Assert.All(page.Groups, group => Assert.Equal(1, group.Values[0].Int64Value));
        Assert.Equal(900, page.Groups.Sum(group => group.Values[2].Int64Value));

        var reference = await ReferenceAsync(context.Store, dataset, plan, aggregate);
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
    }

    /// <summary>
    /// The <c>having</c> clause and the page over the groups that survive it
    /// are the grouped statement's own <c>HAVING</c> and <c>LIMIT</c>
    /// (ADR-0128), so they cost no rows the store would otherwise read. The
    /// assertions are the reference's — the clause over a statistic's result
    /// name, the clause over the group key, the cap that leaves more and the
    /// start that reaches the end — beside a call count, because a store that
    /// read the groups and filtered them afterwards would agree with every one
    /// of them and have pushed nothing.
    /// </summary>
    [SkippableFact]
    public async Task A_group_clause_and_a_group_page_are_a_having_and_a_limit_and_agree_with_the_reference()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var plan = new FeatureQuery(Order: [new OrderTerm("city")]);
        var groups = new[] { "alpha", "bravo", "charlie", "a-null" };

        // Only "alpha" (300 over two rows) and "charlie" (400) clear a sum of
        // 250, and so does the null-keyed group (300): the clause is over a
        // *reduced* value, so it is a comparison the dialect evaluates per
        // group. "bravo" reduces to no sum at all, and a null satisfies no
        // comparison.
        var filtered = new AggregateQuery(
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            ["city"],
            Having: new Predicate.Compare(new FieldRef("total"), ComparisonOperator.GreaterThan, Literal.FromInteger("250")));
        var byResult = await new CountingStore(context.Store).AggregateAsync(dataset, plan, filtered);
        Assert.Equal(["alpha", "charlie", "-"], byResult.Groups.Select(group => group.Key[0].IsNull ? "-" : group.Key[0].StringValue));
        Assert.Equal(Render((await ReferenceAsync(context.Store, dataset, plan, filtered)).Groups), Render(byResult.Groups));

        // The same clause over the group key, which is the column the GROUP BY
        // already carries — and it compares strings by bytes, not by the
        // database's collation (ADR-0121).
        var byKey = filtered with
        {
            Having = new Predicate.Compare(new FieldRef("city"), ComparisonOperator.GreaterThan, Literal.FromText("b")),
        };
        var keyed = await new CountingStore(context.Store).AggregateAsync(dataset, plan, byKey);
        Assert.Equal(["bravo", "charlie"], keyed.Groups.Select(group => group.Key[0].StringValue));
        Assert.Equal(Render((await ReferenceAsync(context.Store, dataset, plan, byKey)).Groups), Render(keyed.Groups));

        // A cap that leaves more, and the flag that says so; then the start
        // that reaches the end of the group set and no flag.
        var paged = new AggregateQuery(
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            ["city"],
            Limit: 2);
        var first = await new CountingStore(context.Store).AggregateAsync(dataset, plan, paged);
        Assert.Equal(groups.Take(2), first.Groups.Select(group => group.Key[0].IsNull ? "a-null" : group.Key[0].StringValue));
        Assert.True(first.HasMore);
        Assert.Equal(Render((await ReferenceAsync(context.Store, dataset, plan, paged)).Groups), Render(first.Groups));

        var last = await new CountingStore(context.Store).AggregateAsync(dataset, plan, paged with { Limit = 2, Offset = 2 });
        Assert.Equal(groups.Skip(2), last.Groups.Select(group => group.Key[0].IsNull ? "a-null" : group.Key[0].StringValue));
        Assert.False(last.HasMore);
        Assert.Equal(Render((await ReferenceAsync(context.Store, dataset, plan, paged with { Limit = 2, Offset = 2 })).Groups), Render(last.Groups));
    }

    /// <summary>
    /// The layer extent is this reduction, so the case is worth its own
    /// assertion: one aggregate row out of the database, no feature read
    /// behind it, and the box the reference takes over the same table
    /// (ADR-0120). A store that answered it by projecting the geometry column
    /// and unioning the rows in managed code would pass every other case here
    /// and fail this one.
    /// </summary>
    [SkippableFact]
    public async Task A_layer_extent_is_one_extent_aggregate_and_no_row_is_read()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);

        var page = await counting.AggregateAsync(
            dataset,
            FeatureQuery.All,
            new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "geom", "box")]));

        Assert.Equal(0, counting.Scans);
        Assert.Equal(1, counting.Aggregates);

        // The five points are (1,1) to (5,5), the third of them with a
        // nullable geometry the extent skips.
        var extent = Assert.Single(page.Groups).Values[0];
        Assert.Equal(AttributeKind.Envelope, extent.Kind);
        Assert.Equal(new Envelope(1, 1, 5, 5), extent.EnvelopeValue);
    }

    /// <summary>
    /// A geometry column whose every value is null is the empty set, not a
    /// rectangle at the origin: the reduction answers null and the caller reads
    /// that as the empty extent, which is what the union over no geometry was.
    /// </summary>
    [SkippableFact]
    public async Task An_envelope_of_no_geometry_is_null_and_not_the_origin()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var none = new FeatureQuery(
            Where: new Predicate.Compare(
                new FieldRef("population"),
                ComparisonOperator.GreaterThan,
                Literal.FromNumber(double.MaxValue)));

        var page = await counting.AggregateAsync(
            dataset,
            none,
            new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "geom", "box")]));

        Assert.Equal(0, counting.Scans);
        Assert.Equal(AttributeValue.Null, Assert.Single(page.Groups).Values[0]);
    }

    [SkippableFact]
    public async Task A_reduction_of_no_rows_is_one_row_of_nulls_ungrouped_and_no_rows_grouped()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var none = new FeatureQuery(
            Where: new Predicate.Compare(
                new FieldRef("population"),
                ComparisonOperator.GreaterThan,
                Literal.FromNumber(double.MaxValue)));

        var ungrouped = await counting.AggregateAsync(dataset, none, new AggregateQuery(Specs));
        var group = Assert.Single(ungrouped.Groups);
        Assert.All(group.Values, value => Assert.Equal(AttributeValue.Null, value));

        var grouped = await counting.AggregateAsync(
            dataset,
            none with { Order = [new OrderTerm("city")] },
            new AggregateQuery(Specs, ["city"]));
        Assert.Empty(grouped.Groups);

        Assert.Equal(0, counting.Scans);
    }

    private static Feature Row(long id, string? city, long? population, double x) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            city is null ? AttributeValue.Null : AttributeValue.FromString(city),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, x, CoordinateReference.Epsg(4326))),
        ]);

    private static async Task<string> SeedAsync(PostgisTestContext context)
    {
        var dataset = $"public.statistics_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} ("
            + "\"id\" bigint PRIMARY KEY, \"city\" text, \"population\" bigint, "
            + "\"geom\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, Rows));
        return dataset;
    }

    /// <summary>
    /// The same reduction as the shared reference computes it: the store's own
    /// rows, reduced in memory by <see cref="FeatureReduction"/>.
    /// </summary>
    private static async Task<AggregatePage> ReferenceAsync(
        PostgisStore store, string dataset, FeatureQuery plan, AggregateQuery aggregate)
    {
        var scan = await store.ScanAsync(dataset);
        return FeatureReduction.Aggregate(
            scan[0].Schema,
            FeaturePlanExecutor.Select(scan[0].Schema, scan.SelectMany(batch => batch.Features).ToList(), plan, CancellationToken.None),
            aggregate,
            plan.Order);
    }

    private static string[] Render(IEnumerable<AggregateGroup> groups) =>
        [.. groups.Select(group => string.Join("|", group.Key.Concat(group.Values).Select(Text)))];

    private static string Text(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Double => value.DoubleValue.ToString("G12", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.IsNull ? "-" : value.ToString(),
    };

    /// <summary>
    /// The store under a call count, so "did it read the table and reduce the
    /// rows in managed code?" is an assertion rather than a claim.
    /// </summary>
    private sealed class CountingStore(IFeatureStore inner) : IFeatureStore, IFeatureAggregateStore
    {
        public int Scans { get; private set; }

        public int Aggregates { get; private set; }

        public Task<FeatureQueryPage> QueryAsync(
            string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            inner.QueryAsync(dataset, query, cancellationToken);

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            Scans++;
            return inner.ScanAsync(dataset, cancellationToken);
        }

        public Task<int> WriteAsync(
            string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(dataset, batch, transaction, cancellationToken);

        public async Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
        {
            Aggregates++;
            return await ((IFeatureAggregateStore)inner).AggregateAsync(dataset, query, aggregate, cancellationToken);
        }

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            ((IFeatureAggregateStore)inner).CountAsync(dataset, query, cancellationToken);

        public Task<DistinctPage> DistinctAsync(
            string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
            ((IFeatureAggregateStore)inner).DistinctAsync(dataset, query, distinct, cancellationToken);
    }
}
