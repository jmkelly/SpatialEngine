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
    /// exceeded-limit flag, and the null-keyed group is one of the groups it
    /// filters (SpatialEngine-u2x.9.4).
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_with_having_and_paging_is_written_the_same_way()
    {
        await AssertSameAsTheMatchPathAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("having", "total > 150"),
            ("resultRecordCount", "1"));
    }

    /// <summary>
    /// A null group key is an ordinary group: the <c>having</c> row schema the
    /// writer builds for the clause declares the group fields and the statistic
    /// results nullable, so a clause over the null-keyed group is
    /// <em>evaluated</em> rather than refused by the feature's own nullability
    /// check (SpatialEngine-u2x.9.4).
    /// </summary>
    /// <remarks>
    /// The request carries a <c>time</c>, which the plan cannot state, so this
    /// is the query that keeps the match path — the one that builds the clause's
    /// row itself rather than handing it to a reduction.
    /// </remarks>
    [Fact]
    public async Task A_having_clause_over_a_null_group_key_is_evaluated_not_refused()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("time", "1,1"),
            ("having", "name IS NULL"),
            ("f", "json"),
        };

        var body = await BodyAsync(Layer(), new MatchStore(Rows), await ParseAsync(parameters));

        // The one null-keyed group, and the 300 it reduced.
        Assert.Equal(1, CountOccurrences(body, "\"total\":"));
        Assert.Contains("\"name\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"total\":300", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reading of a null is the predicate compiler's, and it is the one the
    /// SQL back ends give (ADR-0128): a comparison against a null group key is
    /// <em>unknown</em>, and an unknown is not true, so the null-keyed group is
    /// filtered out rather than kept or treated as an error. The clause still
    /// decides the groups whose keys compare, which is what makes the null one a
    /// filtered group and not a refused query.
    /// </summary>
    [Fact]
    public async Task A_null_group_key_compares_unknown_and_is_filtered_out()
    {
        var body = await BodyAsync(
            Layer(),
            new MatchStore(Rows),
            await ParseAsync(
                ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("time", "1,1"),
                ("having", "name < 'b'"),
                ("f", "json")));

        // "a" and "A" are under 'b', "b" is not under itself, and the null key
        // is unknown — so the null-keyed group is one of the groups dropped.
        Assert.Equal(2, CountOccurrences(body, "\"total\":"));
        Assert.DoesNotContain("\"name\":null", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same reading on the statistic side: a reduction with no sample
    /// variance to report is a null, and a clause over it is evaluated rather
    /// than refused. Every group in the fixture reduces to a single value, so
    /// every <c>var</c> is null and the clause keeps every group.
    /// </summary>
    [Fact]
    public async Task A_null_statistic_result_is_compared_under_having()
    {
        var body = await BodyAsync(
            Layer(),
            new MatchStore(Rows),
            await ParseAsync(
                ("outStatistics", """[{"statisticType":"var","onStatisticField":"population","outStatisticFieldName":"var"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("time", "1,1"),
                ("having", "var IS NULL"),
                ("f", "json")));

        Assert.Equal(4, CountOccurrences(body, "\"var\":"));
        Assert.Contains("\"var\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":null", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same reading on the pushed path, against the match path's answer to
    /// the same question: the clause filters the null-keyed group the same way
    /// whether the store's reduction answered it or the writer did.
    /// </summary>
    [Fact]
    public async Task A_null_group_key_reads_the_same_way_pushed_down_and_in_memory()
    {
        foreach (var clause in new[] { "name IS NULL", "name < 'b'", "name > 'a'" })
        {
            var pushed = await ParseAsync(
                ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("orderByFields", "name ASC"),
                ("having", clause),
                ("f", "json"));

            // A `time` is what the plan cannot state, so the same served request
            // against a store with no reduction face is reduced by the writer.
            var matched = await ParseAsync(
                ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("orderByFields", "name ASC"),
                ("having", clause),
                ("time", "1,1"),
                ("f", "json"));

            Assert.Equal(
                await BodyAsync(Layer(), new MatchStore(Rows), matched),
                await BodyAsync(Layer(), new AggregatingStore(Rows), pushed));
        }
    }

    /// <summary>
    /// A group of one row is the one case where a pushed-down reduction and
    /// the reference could disagree about a number: the sample variance
    /// divides by n − 1, so a single value has no sample variance, and SQL's
    /// <c>VAR_SAMP</c>/<c>STDDEV_SAMP</c> answer null where a hand-rolled
    /// reduction that guards the division answers zero. The reference says
    /// null — the population form of a group the sample form cannot describe
    /// is not the statistic that was asked for — and both paths answer it.
    /// </summary>
    [Fact]
    public async Task A_group_of_one_row_has_no_sample_variance_and_answers_null_on_both_paths()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics",
                """[{"statisticType":"var","onStatisticField":"population","outStatisticFieldName":"var"},{"statisticType":"stddev","onStatisticField":"population","outStatisticFieldName":"sd"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("f", "json"),
        };

        await AssertSameAsTheMatchPathAsync(parameters);

        // Every group in this fixture reduces to a single non-null population,
        // so every one of them is undefined and every one answers null — with
        // the row count beside it, so the row is a reduction and not the
        // empty-set row of nulls.
        var body = await BodyAsync(Layer(), new AggregatingStore(Rows), await ParseAsync(parameters));
        Assert.Contains("\"var\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"sd\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"n\":1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"var\":0", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The S3 percentiles are the statistics most likely to disagree between a
    /// dialect and a hand-rolled interpolation: <c>PERCENTILE_CONT</c> and
    /// <c>PERCENTILE_DISC</c> rank, interpolate and round their own way, and
    /// the reference ranks at <c>f × (n − 1)</c> and <c>ceil(f × n)</c>. Both
    /// directions are compared, because a descending rank order is a second
    /// rule and not a flag.
    /// </summary>
    [Fact]
    public async Task Every_percentile_reduces_the_same_way_pushed_down_and_in_memory()
    {
        const string Continuous =
            """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.9},"onStatisticField":"population","outStatisticFieldName":"p90"}]""";
        const string Both =
            """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.9},"onStatisticField":"population","outStatisticFieldName":"p90"},{"statisticType":"percentile_disc","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]""";
        const string Descending =
            """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.9,"orderBy":"DESC"},"onStatisticField":"population","outStatisticFieldName":"p90d"}]""";

        await AssertSameAsTheMatchPathAsync(("where", "name IS NOT NULL"), ("outStatistics", Both));
        await AssertSameAsTheMatchPathAsync(("outStatistics", Descending));

        // Two distinct values in the group, so the continuous percentile has to
        // interpolate between them: 100 and 300 at 0.9 is 280.
        var body = await BodyAsync(
            Layer(),
            new AggregatingStore([Row(1, "a", 100, 1, 1), Row(2, "a", 300, 2, 2)]),
            await ParseAsync(("outStatistics", Continuous), ("groupByFieldsForStatistics", "name"), ("f", "json")));

        Assert.Contains("\"p90\":280", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A statistics response pages and filters <em>groups</em>, not features, so
    /// the cap and the page start apply to the rows the reduction returned: the
    /// same rows, the same order, the same <c>exceededTransferLimit</c> and the
    /// same continuation token, whether the groups came from a store or from a
    /// materialised match set.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_paged_over_its_groups_is_written_the_same_way()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("resultOffset", "1"),
            ("resultRecordCount", "1"),
            ("f", "json"),
        };

        await AssertSameAsTheMatchPathAsync(parameters);

        var body = await BodyAsync(Layer(), new AggregatingStore(Rows), await ParseAsync(parameters));

        // The four groups, the second one only, and the token for the third.
        Assert.Equal(1, CountOccurrences(body, "\"total\":"));
        Assert.Contains("\"exceededTransferLimit\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"resultPaginationToken\"", body, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The page over groups and the <c>having</c> clause are the store's
    /// reduction to answer, not the writer's: the reduction carries the clause
    /// as a predicate over the group row (the group fields and the statistics'
    /// result names) and the page as its own cap and start, because a cap the
    /// <em>plan</em> asked for would cut rows the store never grouped
    /// (ADR-0128). The cap is asked for on every statistics request, so a store
    /// that ignored the plan's page would be over-read on the most ordinary
    /// query there is.
    /// </summary>
    [Fact]
    public async Task The_group_page_and_the_having_clause_are_asked_of_the_store()
    {
        var store = new AggregatingStore(Rows);
        await BodyAsync(
            Layer(),
            store,
            await ParseAsync(
                ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
                ("groupByFieldsForStatistics", "name"),
                ("orderByFields", "name ASC"),
                ("having", "total > 150"),
                ("resultOffset", "1"),
                ("resultRecordCount", "2"),
                ("f", "json")));

        var reduction = Assert.IsType<AggregateQuery>(store.LastReduction);
        Assert.Equal(2, reduction.Limit);
        Assert.Equal(1, reduction.Offset);
        var clause = Assert.IsType<Predicate.Compare>(reduction.Having);
        Assert.Equal("total", clause.Field.Name);
        Assert.Equal(ComparisonOperator.GreaterThan, clause.Operator);
        Assert.Equal(1, store.Aggregates);
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// A <c>having</c> clause that names neither a statistic nor a group field
    /// is a clause over nothing, and the pushed path rejects it the same way
    /// the match path does rather than answering a page of every group.
    /// </summary>
    [Fact]
    public async Task A_having_clause_over_an_unknown_name_fails_the_same_way_on_both_paths()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("having", "missing > 1"),
            ("f", "json"),
        };

        var pushed = await Assert.ThrowsAsync<EsriInteropException>(
            async () => await BodyAsync(Layer(), new AggregatingStore(Rows), await ParseAsync(parameters)));
        var matched = await Assert.ThrowsAsync<EsriInteropException>(
            async () => await BodyAsync(Layer(), new MatchStore(Rows), await ParseAsync(parameters)));

        Assert.Contains("missing", pushed.Message, StringComparison.Ordinal);
        Assert.Equal(matched.Message, pushed.Message);
    }

    /// <summary>
    /// The store's page is the page: the rows it returned are the rows written,
    /// and its own "one more group" answer is the <c>exceededTransferLimit</c>
    /// flag — the adapter no longer skips and takes over the whole group set, so
    /// a store that returns exactly the page and says so is served without a
    /// second question.
    /// </summary>
    [Fact]
    public async Task The_store_s_group_page_is_the_page_the_response_writes()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("orderByFields", "name ASC"),
            ("resultOffset", "1"),
            ("resultRecordCount", "1"),
            ("f", "json"),
        };

        await AssertSameAsTheMatchPathAsync(parameters);

        var body = await BodyAsync(Layer(), new AggregatingStore(Rows), await ParseAsync(parameters));
        Assert.Equal(1, CountOccurrences(body, "\"total\":"));
        Assert.Contains("\"exceededTransferLimit\":true", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unordered grouped reduction is the store's to answer (ADR-0194). The
    /// plan asks for no group order — no <c>orderByFields</c> — and the
    /// contract's answer for a reduction the plan did not order is the store's
    /// own order (ADR-0098 §3, ADR-0128 §8): a store that reduces over the rows
    /// it read keeps the reference's first-seen order, so the answer is the
    /// match path's answer and the whole layer never has to be scanned. This is
    /// the natural client request — <c>outStatistics</c> +
    /// <c>groupByFieldsForStatistics</c> and nothing else — and it is the one
    /// SpatialEngine-d0q found routing to the match path.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_with_no_order_is_asked_of_the_store()
    {
        var store = new AggregatingStore(Rows);
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("f", "json"));

        var body = await BodyAsync(Layer(), store, query);

        Assert.Equal(1, store.Aggregates);
        Assert.Equal(0, store.Scans);
        Assert.Null(Assert.IsType<FeatureQuery>(store.LastPlan).Order);
        Assert.Equal(["name"], Assert.IsType<AggregateQuery>(store.LastReduction).GroupBy);
        Assert.Equal(await BodyAsync(Layer(), new MatchStore(Rows), query), body);
    }

    /// <summary>
    /// The <c>having</c> clause and the group page ride on the unordered
    /// reduction the same way they do on an ordered one (ADR-0128): the
    /// store filters and pages the groups it reduced, and the served response
    /// is the match path's answer for the same request.
    /// </summary>
    [Fact]
    public async Task An_unordered_grouped_reduction_carries_the_having_clause_and_the_page()
    {
        var parameters = new (string Key, string Value)[]
        {
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name"),
            ("having", "total > 150"),
            ("resultRecordCount", "1"),
            ("f", "json"),
        };

        await AssertSameAsTheMatchPathAsync(parameters);

        var store = new AggregatingStore(Rows);
        await BodyAsync(Layer(), store, await ParseAsync(parameters));

        var reduction = Assert.IsType<AggregateQuery>(store.LastReduction);
        Assert.Equal(1, reduction.Limit);
        Assert.NotNull(reduction.Having);
        Assert.Equal(1, store.Aggregates);
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// A group order the plan <em>is</em> asked for but cannot state is still
    /// not a reduction to push: the served response would be an order SQL
    /// chose over a match set it never assembled, where the response owes the
    /// order the request asked for (ADR-0098 §3). An order over one group field
    /// of a two-field group is not a total order over the groups, so the
    /// request keeps the match path.
    /// </summary>
    [Fact]
    public async Task A_grouped_reduction_ordered_by_something_else_keeps_the_match_path()
    {
        var store = new AggregatingStore(Rows);
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"}]"""),
            ("groupByFieldsForStatistics", "name,population"),
            ("orderByFields", "name ASC"),
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
            Task.FromResult(FeatureReduction.Distinct(Schema, FeaturePlanExecutor.Select(Schema, features, query, cancellationToken), distinct, query.Order));

        public Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Aggregates++;
            LastPlan = query;
            LastReduction = aggregate;
            return Task.FromResult(
                FeatureReduction.Aggregate(
                    Schema,
                    FeaturePlanExecutor.Select(Schema, features, query, cancellationToken),
                    aggregate,
                    query.Order));
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
