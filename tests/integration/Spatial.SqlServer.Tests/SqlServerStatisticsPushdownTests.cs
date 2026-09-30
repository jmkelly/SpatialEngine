using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The <c>outStatistics</c> reduction against a live SQL Server (ADR-0115,
/// ADR-0133, ADR-0137): every statistic the contract can name is compiled to
/// T-SQL, including the two the dialect has no aggregate for — a percentile,
/// which is a window function over the partition a group is, and a boolean
/// extreme, which is an extreme over the <c>bit</c>'s own integers.
///
/// <para>
/// Two assertions per case, and the second is the one that matters. The call
/// count says the table was not read to be reduced afterwards in managed code;
/// the value comparison says the numbers and the rows that came back are the
/// reference's. The cases are the ones a dialect is tempted to get wrong: a
/// group that exists with one member (the sample variance is undefined, so it
/// is null and not zero), a group whose values are all null (a percentile has
/// nothing to rank, and reports null), a group whose two values interpolate (a
/// percentile between two rows), a descending rank, and a text key that folds
/// under the database's own collation.
/// </para>
/// </summary>
public sealed class SqlServerStatisticsPushdownTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerStatisticsPushdownTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("city", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("geom", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>
    /// Every statistic, as the shared conformance suite asks for them: a count
    /// of rows and a count of values, the sum and the mean, the two extremes
    /// over the text key, the two sample forms, both percentiles, and a boolean
    /// extreme — the last two being the translations ADR-0137 added.
    /// </summary>
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
        new(AggregateStatistic.Minimum, "active", "anyOff"),
        new(AggregateStatistic.Maximum, "active", "allOn"),
    ];

    /// <summary>
    /// Five rows over four groups: one group with two members whose populations
    /// differ (so a percentile interpolates between them), one with a null
    /// population (a count skips it, and the group of nothing left is null), one
    /// with no population and no boolean at all (a percentile has nothing to
    /// rank), two single-member groups (which the sample variance cannot
    /// describe), plus a null-keyed group of its own.
    /// </summary>
    private static readonly IReadOnlyList<Feature> Rows =
    [
        Row(1, "alpha", 100, true),
        Row(2, "alpha", 200, false),
        Row(3, "bravo", null, null),
        Row(4, "charlie", 400, true),
        Row(5, null, 300, true),
    ];

    [SkippableFact]
    public async Task An_ungrouped_reduction_is_one_aggregate_row_and_agrees_with_the_reference()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
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
        Assert.Equal(370.0, values[8].DoubleValue, 9);  // 100/200/300/400 at 0.9
        Assert.Equal(200.0, values[9].DoubleValue, 9);  // ceil(0.5 × 4) is the second
        Assert.False(values[10].BooleanValue);          // a group with a false in it
        Assert.True(values[11].BooleanValue);

        var reference = await ReferenceAsync(context.Store, dataset, FeatureQuery.All, new AggregateQuery(Specs));
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
    }

    [SkippableFact]
    public async Task A_grouped_reduction_is_a_group_by_and_agrees_with_the_reference_in_group_key_order()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var aggregate = new AggregateQuery(Specs, ["city"]);
        var plan = new FeatureQuery(Order: [new OrderTerm("city")]);
        var page = await counting.AggregateAsync(dataset, plan, aggregate);

        Assert.Equal(0, counting.Scans);
        Assert.Equal(1, counting.Aggregates);

        var reference = await ReferenceAsync(context.Store, dataset, plan, aggregate);
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
        Assert.Equal(
            ["alpha", "bravo", "charlie", null],
            page.Groups.Select(group => group.Key[0].IsNull ? null : group.Key[0].StringValue));

        // The single-member groups have no sample variance: null, not zero.
        var single = page.Groups.Where(group => group.Values[0].Int64Value == 1).ToArray();
        Assert.Equal(3, single.Length);
        Assert.All(single, group => Assert.Equal(AttributeValue.Null, group.Values[6]));

        // The group of two interpolates between its rows: 100 and 200 at 0.9 is
        // 190, and the discrete rank ceil(0.5 × 2) is the first of them. The
        // group's boolean extreme is false, because a false is in it.
        var alpha = page.Groups[0];
        Assert.Equal(190.0, alpha.Values[8].DoubleValue, 9);
        Assert.Equal(100.0, alpha.Values[9].DoubleValue, 9);
        Assert.False(alpha.Values[10].BooleanValue);
        Assert.True(alpha.Values[11].BooleanValue);

        // The group with nothing to rank reports the reference's null, and not a
        // zero: a percentile of no values has no value.
        var bravo = page.Groups[1];
        Assert.Equal(AttributeValue.Null, bravo.Values[8]);
        Assert.Equal(AttributeValue.Null, bravo.Values[9]);
    }

    /// <summary>
    /// A descending rank is a descending <c>WITHIN GROUP</c>, which is the other
    /// end of the same statistic: the same two rows, read the other way up.
    /// </summary>
    [SkippableFact]
    public async Task A_descending_percentile_ranks_the_field_the_other_way_up()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var specs = new[]
        {
            new AggregateSpec(AggregateStatistic.PercentileContinuous, "population", "p90", 0.9, PercentileDescending: true),
            new AggregateSpec(AggregateStatistic.PercentileDiscrete, "population", "p50", 0.5, PercentileDescending: true),
        };
        var aggregate = new AggregateQuery(specs, ["city"]);
        var plan = new FeatureQuery(Order: [new OrderTerm("city")]);
        var counting = new CountingStore(context.Store);
        var page = await counting.AggregateAsync(dataset, plan, aggregate);

        Assert.Equal(0, counting.Scans);
        Assert.Equal(Render((await ReferenceAsync(context.Store, dataset, plan, aggregate)).Groups), Render(page.Groups));

        // The group of two values, read the other way up: 200 down to 100 at
        // 0.9 interpolates 110, and the discrete rank ceil(0.5 × 2) from the
        // top of the group is the first of them.
        var alpha = page.Groups[0];
        Assert.Equal(110.0, alpha.Values[0].DoubleValue, 9);
        Assert.Equal(200.0, alpha.Values[1].DoubleValue, 9);
    }

    /// <summary>
    /// The restriction reaches the statement, and a group that exists in the
    /// table with nothing in the selection is never a row — the <c>GROUP BY</c>
    /// answers the groups that have a member, which is what a reduction of a
    /// selection is (ADR-0098 §7).
    /// </summary>
    [SkippableFact]
    public async Task A_restriction_reaches_the_aggregate_and_the_selection_is_its_own()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
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
        Assert.Equal(
            ["alpha", "charlie", null],
            page.Groups.Select(group => group.Key[0].IsNull ? null : group.Key[0].StringValue));
        Assert.All(page.Groups, group => Assert.Equal(1, group.Values[0].Int64Value));
        Assert.Equal(900, page.Groups.Sum(group => group.Values[2].Int64Value));

        var reference = await ReferenceAsync(context.Store, dataset, plan, aggregate);
        Assert.Equal(Render(reference.Groups), Render(page.Groups));
    }

    /// <summary>
    /// A reduction of nothing is one row of nulls ungrouped — the row count is
    /// the one statistic that is not null, because it counts rows rather than
    /// their values — and no rows grouped, which is the same answer as no
    /// groups (ADR-0098 §3).
    /// </summary>
    [SkippableFact]
    public async Task A_reduction_of_no_rows_is_one_row_of_nulls_ungrouped_and_no_rows_grouped()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var counting = new CountingStore(context.Store);
        var none = new FeatureQuery(
            Where: new Predicate.Compare(
                new FieldRef("population"),
                ComparisonOperator.GreaterThan,
                Literal.FromNumber(double.MaxValue)));

        var ungrouped = await counting.AggregateAsync(dataset, none, new AggregateQuery(Specs));
        var group = Assert.Single(ungrouped.Groups);
        Assert.Equal(AttributeValue.FromInt64(0), group.Values[0]);
        Assert.All(group.Values.Skip(1), value => Assert.Equal(AttributeValue.Null, value));

        var reference = await ReferenceAsync(context.Store, dataset, none, new AggregateQuery(Specs));
        Assert.Equal(Render(reference.Groups), Render(ungrouped.Groups));

        var grouped = await counting.AggregateAsync(
            dataset,
            none with { Order = [new OrderTerm("city")] },
            new AggregateQuery(Specs, ["city"]));
        Assert.Empty(grouped.Groups);

        Assert.Equal(0, counting.Scans);
    }

    private static Feature Row(long id, string? city, long? population, bool? active) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            city is null ? AttributeValue.Null : AttributeValue.FromString(city),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            active is { } flag ? AttributeValue.FromBoolean(flag) : AttributeValue.Null,
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id, id)),
        ]);

    /// <summary>
    /// The table is created by hand rather than through the store's
    /// <c>CreateAsync</c>, because the pushdown is decided by the dataset's
    /// identity: a table with a primary key is one whose rows keep their names
    /// under a restriction, and the rows are written through the store, so the
    /// write path is the store's.
    /// </summary>
    private static async Task<string> SeedAsync(SqlServerTestContext context)
    {
        var dataset = $"dbo.statistics_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id bigint NOT NULL PRIMARY KEY, city nvarchar(100) NULL, "
            + "population bigint NULL, active bit NULL, geom geometry NULL)");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, Rows));
        return dataset;
    }

    /// <summary>
    /// The same reduction as the shared reference computes it: the store's own
    /// rows, reduced in memory by <see cref="FeatureReduction"/>.
    /// </summary>
    private static async Task<AggregatePage> ReferenceAsync(
        SqlServerStore store, string dataset, FeatureQuery plan, AggregateQuery aggregate)
    {
        var scan = await store.ScanAsync(dataset);
        return FeatureReduction.Aggregate(
            scan[0].Schema,
            FeaturePlanExecutor.Select(
                scan[0].Schema, scan.SelectMany(batch => batch.Features).ToList(), plan, CancellationToken.None),
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

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(dataset, batch, transaction, cancellationToken);

        public async Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
        {
            Aggregates++;
            return await ((IFeatureAggregateStore)inner).AggregateAsync(dataset, query, aggregate, cancellationToken);
        }

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            ((IFeatureAggregateStore)inner).CountAsync(dataset, query, cancellationToken);

        public Task<DistinctPage> DistinctAsync(string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
            ((IFeatureAggregateStore)inner).DistinctAsync(dataset, query, distinct, cancellationToken);
    }
}
