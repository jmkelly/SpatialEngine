using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Querying;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// SpatialEngine-u2x.9.2: the <c>outStatistics</c> reduction is asked of the
/// store's own face (ADR-0098 §7) instead of being grouped over a materialised
/// match set, and the answer has to be the answer the match path would have
/// written — byte for byte.
///
/// <para>
/// Every test here is a pair: the same served request against a store that
/// implements <see cref="IFeatureAggregateStore"/> and against one that does
/// not, and the two response bodies are compared as text. That is the whole
/// invariant — a pushdown that preserves the values but reorders the groups,
/// nulls one out, collates the names differently or answers the empty set with
/// a zero instead of a null row has failed, whatever its numbers are. The
/// assertions beside the comparison say which of those each case is about.
/// </para>
///
/// <para>
/// The fixture is built to be awkward: a null group key, names whose ordinal
/// order is not their alphabetical one, and rows whose scan order is not the
/// order any plan asked for.
/// </para>
/// </summary>
public sealed class FeatureStatisticsPushdownTests
{
    private static QueryServices Services => new(Operations!, Relations!, new NtsGeometryMeasures(), Transforms!, Transforms!);

    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription Layer() => new(
        "demo.places", "demo", "places", "geometry", 4326, "Point", 4, ["id"], Schema);

    private static Feature Row(long id, string? name, long? population, double x, double y) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            name is null ? AttributeValue.Null : AttributeValue.FromString(name),
            population is null ? AttributeValue.Null : AttributeValue.FromInt64(population.Value),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    /// <summary>
    /// The scan order is deliberately not any order a plan could ask for: the
    /// groups are met in the order "b", "a" (lower), "A", then the null key, so
    /// a pushdown that returns the store's own group order and a pushdown that
    /// returns the plan's are two different answers, and only one of them is
    /// served today.
    /// </summary>
    private static IReadOnlyList<Feature> Rows { get; } =
    [
        Row(1, "b", 100, 13.4, 52.5),
        Row(2, "a", 200, 2.35, 48.85),
        Row(3, "A", 200, 12.5, 41.9),
        Row(4, null, 300, 4.9, 50.1),
        Row(5, "b", null, 8.7, 50.8),
    ];

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriFeatureQuery.Parse(await EsriRequestParameters.ReadAsync(context, CancellationToken.None), Crs4326);
    }

    private static async Task<string> BodyAsync(DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
    {
        var result = await FeatureService.QueryAsync(dataset, store, query, Services, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    /// <summary>The served body from a store with the reduction face, and from one without: the same text is the whole contract.</summary>
    private static async Task AssertSameAsTheMatchPathAsync(params (string Key, string Value)[] parameters)
    {
        var query = await ParseAsync([.. parameters, ("f", "json")]);

        Assert.Equal(
            await BodyAsync(Layer(), new MatchStore(Rows), query),
            await BodyAsync(Layer(), new AggregatingStore(Rows), query));
    }

    [Fact]
    public async Task An_ungrouped_reduction_is_asked_of_the_store_and_the_dataset_is_never_scanned()
    {
        var store = new AggregatingStore(Rows);
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
            ("f", "json"));

        var body = await BodyAsync(Layer(), store, query);

        Assert.Equal(1, store.Aggregates);
        Assert.Equal(0, store.Scans);
        Assert.Contains("\"total\":800", body, StringComparison.Ordinal);
        Assert.Contains("\"n\":5", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The request's statistics are the store's reduction vocabulary name for
    /// name, and the plan carries the match whole: the clause as its predicate,
    /// the geometry's envelope as its box, and no page and no projection —
    /// a statistics response pages and filters <em>groups</em>, which the store
    /// must not cut into rows it never grouped.
    /// </summary>
    [Fact]
    public async Task A_restricted_reduction_is_pushed_onto_the_plan_as_the_store_states_it()
    {
        var store = new AggregatingStore(Rows);
        await BodyAsync(
            Layer(),
            store,
            await ParseAsync(
                ("where", "population > 150"),
                ("outFields", "name"),
                ("outStatistics", """[{"statisticType":"avg","onStatisticField":"population","outStatisticFieldName":"mean"},{"statisticType":"count","onStatisticField":"population","outStatisticFieldName":"scored"}]"""),
                ("f", "json")));

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        Assert.NotNull(plan.Where);
        Assert.Null(plan.Projection);
        Assert.Null(plan.Limit);
        Assert.Null(plan.Offset);

        var reduction = Assert.IsType<AggregateQuery>(store.LastReduction);
        Assert.Null(reduction.GroupBy);
        Assert.Equal(
            [AggregateStatistic.Average, AggregateStatistic.Count],
            reduction.Specs.Select(spec => spec.Statistic).Take(2));
        Assert.Equal(["mean", "scored"], reduction.Specs.Take(2).Select(spec => spec.Name).ToArray());
    }

    [Fact]
    public async Task Every_statistic_type_reduces_the_same_way_pushed_down_and_in_memory()
    {
        await AssertSameAsTheMatchPathAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"avg","onStatisticField":"population","outStatisticFieldName":"mean"},{"statisticType":"min","onStatisticField":"name","outStatisticFieldName":"lo"},{"statisticType":"max","onStatisticField":"name","outStatisticFieldName":"hi"},{"statisticType":"var","onStatisticField":"population","outStatisticFieldName":"var"},{"statisticType":"stddev","onStatisticField":"population","outStatisticFieldName":"sd"}]"""));
    }

    /// <summary>
    /// The empty-set rule is the one Esri response shape a reduction cannot
    /// reproduce on its own: an ungrouped statistics query over no rows answers
    /// with one row of nulls — including the row count, which is null there and
    /// not zero, because zero is what a reduction of zero rows computes.
    /// </summary>
    [Theory]
    [InlineData("""[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]""")]
    public async Task A_reduction_over_no_rows_is_still_one_row_of_nulls(string outStatistics)
    {
        var pushed = new AggregatingStore(Rows);
        var body = await BodyAsync(
            Layer(),
            pushed,
            await ParseAsync(("where", "name = 'Nowhere'"), ("outStatistics", outStatistics), ("f", "json")));

        Assert.Equal(1, pushed.Aggregates);
        Assert.Contains("\"features\":[{\"attributes\":{", body, StringComparison.Ordinal);
        Assert.DoesNotContain(":0", body, StringComparison.Ordinal);
        Assert.Equal(await BodyAsync(Layer(), new MatchStore(Rows), await ParseAsync(("where", "name = 'Nowhere'"), ("outStatistics", outStatistics), ("f", "json"))), body);
    }

    /// <summary>
    /// The group order is the order the plan asked for, which SQL has to be told
    /// and a first-seen order over rows SQL never assembled is not (ADR-0098
    /// §3): <c>orderByFields</c> naming exactly the group fields makes the group
    /// keys a total order, so the reduction can be pushed and the writer's
    /// statistic order is that same order.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_in_group_field_order_is_asked_of_the_store()
    {
        var store = new AggregatingStore(Rows);
        await BodyAsync(
            Layer(),
            store,
            await ParseAsync(
                ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("orderByFields", "name ASC"),
                ("f", "json")));

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        Assert.NotNull(plan.Order);
        Assert.Equal(["name"], plan.Order.Select(term => term.Field));
        Assert.Equal(SortDirection.Ascending, plan.Order[0].Direction);
        Assert.Equal(["name"], Assert.IsType<AggregateQuery>(store.LastReduction).GroupBy);
        Assert.Equal(1, store.Aggregates);
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// The fixture's scan order is not the served group order, so a store that
    /// groups in its own order is a red test: the writer's statistic order is
    /// what decides the rows, and it must put the null key last ascending, "A"
    /// before "a" (ordinal, not a locale collation) and every group in the
    /// order the request asked for.
    /// </summary>
    [Fact]
    public async Task A_group_order_the_store_returns_differently_is_written_in_the_requested_order()
    {
        await AssertSameAsTheMatchPathAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"));

        await AssertSameAsTheMatchPathAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name DESC"));
    }

    /// <summary>
    /// <c>having</c> filters the <em>groups</em> the store returned, so it runs
    /// after the reduction rather than inside it, and the page the response
    /// writes is the group page — the same rows, the same token, the same
    /// exceeded-limit flag. The null-keyed group is excluded here because a
    /// <c>having</c> clause over a null group value is a separate defect
    /// (SpatialEngine-u2x.9.4), not this path's business.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_with_having_and_paging_is_written_the_same_way()
    {
        await AssertSameAsTheMatchPathAsync(
            ("where", "name IS NOT NULL"),
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("having", "total > 150"),
            ("resultRecordCount", "1"));
    }

    /// <summary>
    /// A group order the plan cannot state is not a reduction to push: the
    /// groups would come back in a store's own order, where the served response
    /// is the first-seen order of a match set. The request keeps the match path.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_the_plan_cannot_order_keeps_the_match_path()
    {
        var store = new AggregatingStore(Rows);
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("f", "json"));

        var body = await BodyAsync(Layer(), store, query);

        Assert.Equal(0, store.Aggregates);
        Assert.Equal(1, store.Scans);
        Assert.Equal(await BodyAsync(Layer(), new MatchStore(Rows), query), body);
    }

    /// <summary>
    /// Ordering by something that is neither a group field nor a statistic is
    /// the same rejection either way: the writer cannot build a key for it, and
    /// the pushdown must not turn that into an order of its own.
    /// </summary>
    [Fact]
    public async Task An_order_the_writer_has_no_key_for_still_fails_the_same_way()
    {
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "population DESC"),
            ("f", "json"));

        var pushed = await Assert.ThrowsAsync<EsriInteropException>(() => BodyAsync(Layer(), new AggregatingStore(Rows), query));
        var matched = await Assert.ThrowsAsync<EsriInteropException>(() => BodyAsync(Layer(), new MatchStore(Rows), query));

        Assert.Contains("'orderByFields' names unknown statistic or group field 'population'", pushed.Message, StringComparison.Ordinal);
        Assert.Equal(matched.Message, pushed.Message);
    }

    /// <summary>A request the plan cannot carry keeps the match path, as every other verb here does.</summary>
    [Fact]
    public async Task A_request_with_a_time_keeps_the_match_path()
    {
        var store = new AggregatingStore(Rows);
        var body = await BodyAsync(
            Layer(),
            store,
            await ParseAsync(
                ("time", "1,1"),
                ("outStatistics", """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
                ("f", "json")));

        Assert.Equal(0, store.Aggregates);
        Assert.Equal(1, store.Scans);
        Assert.Contains("\"n\":5", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_request_cancels_rather_than_answers()
    {
        var store = new AggregatingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
            ("f", "json"));
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureService.QueryAsync(Layer(), store, query, Services, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    /// <summary>
    /// A store with the whole query surface: it records what it was asked and
    /// answers with the shared reference executor, which is exactly what a
    /// pushing provider must reproduce.
    /// </summary>
    private sealed class AggregatingStore(IReadOnlyList<Feature> features) : IFeatureStore, IFeatureAggregateStore
    {
        public int Scans { get; private set; }

        public int Aggregates { get; private set; }

        public FeatureQuery? LastPlan { get; private set; }

        public AggregateQuery? LastReduction { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(FeaturePlanExecutor.Execute(Schema, features, query, cancellationToken));

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(FeatureReduction.CountFeatures(FeaturePlanExecutor.Select(Schema, features, query, cancellationToken)));

        public Task<DistinctPage> DistinctAsync(
            string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
            Task.FromResult(FeatureReduction.Distinct(Schema, FeaturePlanExecutor.Select(Schema, features, query, cancellationToken), distinct));

        public Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Aggregates++;
            LastPlan = query;
            LastReduction = aggregate;
            return Task.FromResult(
                FeatureReduction.Aggregate(Schema, FeaturePlanExecutor.Select(Schema, features, query, cancellationToken), aggregate));
        }
    }

    /// <summary>A store with no reduction face: the caller reduces what it read.</summary>
    private sealed class MatchStore(IReadOnlyList<Feature> features) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            FeaturePlanFallback.ReadAsync(this, dataset, query, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
