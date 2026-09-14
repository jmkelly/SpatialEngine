using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-037: percentile statistics plus capability-flag honesty, replaying
/// <c>research/compat/ground-truth/feature-layer0.DamageAssessment.json</c>
/// (live layer keys <c>supportsExceedsLimitStatistics</c>,
/// <c>supportsCountDistinct</c>, <c>supportsPercentileStatistics</c>).
/// Red-first: percentile types are rejected by the statistic table today,
/// the count-distinct shape is mutually exclusive, and the layer emits none
/// of the five flags.
/// </summary>
public sealed class EsriFeatureStatsTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();

    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription Layer() => new(
        "demo.places", "demo", "places", "geometry", 4326, "Point", 5, ["id"], Schema);

    private static Feature Row(long id, string name, long population, double x, double y) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(population),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values) =>
        EsriFeatureQuery.Parse(await ParamsAsync(values), Crs4326);

    private static async Task<JsonElement> QueryBodyAsync(DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
    {
        var result = await FeatureService.QueryAsync(dataset, store, query, Operations, Transforms, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class StatsStore : IFeatureStore
    {
        private readonly Feature[] _features;

        public StatsStore(params Feature[] features) => _features = features;

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, _features)];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    // ---- 1. percentile_cont / percentile_disc (S3 percentile type) ----

    [Theory]
    [InlineData("percentile_cont")]
    [InlineData("percentile_disc")]
    [InlineData("PERCENTILE_CONT")]
    public async Task Percentile_statistic_types_parse(string statisticType)
    {
        var query = await ParseAsync(
            ("outStatistics", $$"""[{"statisticType":"{{statisticType}}","statisticParameters":{"value":0.9},"onStatisticField":"population","outStatisticFieldName":"p90"}]"""));

        var statistic = Assert.Single(query.OutStatistics!);
        Assert.StartsWith("percentile", statistic.StatisticType, StringComparison.Ordinal);
        Assert.Equal("population", statistic.OnStatisticField);
        Assert.Equal("p90", statistic.OutStatisticFieldName);
    }

    [Fact]
    public async Task Percentile_descending_order_parses()
    {
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_disc","statisticParameters":{"value":0.9,"orderBy":"DESC"},"onStatisticField":"population","outStatisticFieldName":"p90"}]"""));

        var statistic = Assert.Single(query.OutStatistics!);
        Assert.Equal("percentile_disc", statistic.StatisticType);
        Assert.Equal("p90", statistic.OutStatisticFieldName);
    }

    [Theory]
    [InlineData("""[{"statisticType":"percentile_cont","onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{},"onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{"value":-0.1},"onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{"value":1.5},"onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{"value":"high"},"onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    [InlineData("""[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5,"orderBy":"SIDEWAYS"},"onStatisticField":"population","outStatisticFieldName":"p"}]""")]
    public async Task Percentile_requires_a_unit_value_and_asc_desc_order(string outStatistics)
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("outStatistics", outStatistics)));

        Assert.Contains("statisticParameters", exception.Message);
    }

    [Fact]
    public async Task Percentile_with_having_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]"""),
            ("having", "p50 > 100")));

        Assert.Contains("having", exception.Message);
    }

    [Fact]
    public async Task Statistic_parameters_on_a_non_percentile_type_are_rejected()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            ("outStatistics", """[{"statisticType":"sum","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"s"}]""")));

        Assert.Contains("statisticParameters", exception.Message);
    }

    [Fact]
    public async Task Percentile_cont_interpolates_between_dataset_values()
    {
        var dataset = Layer();
        var store = new StatsStore(
            Row(1, "a", 10, 0, 0), Row(2, "b", 20, 1, 1), Row(3, "c", 30, 2, 2), Row(4, "d", 40, 3, 3));
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]"""));

        var body = await QueryBodyAsync(dataset, store, query);

        var attributes = Assert.Single(body.GetProperty("features").EnumerateArray()).GetProperty("attributes");
        Assert.Equal(25.0, attributes.GetProperty("p50").GetDouble(), 9);
    }

    [Fact]
    public async Task Percentile_disc_returns_a_dataset_value_in_the_requested_order()
    {
        var dataset = Layer();
        var store = new StatsStore(
            Row(1, "a", 10, 0, 0), Row(2, "b", 20, 1, 1), Row(3, "c", 30, 2, 2));
        var ascending = await ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_disc","statisticParameters":{"value":0.9},"onStatisticField":"population","outStatisticFieldName":"p90"}]"""));
        var descending = await ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_disc","statisticParameters":{"value":0.9,"orderBy":"DESC"},"onStatisticField":"population","outStatisticFieldName":"p90"}]"""));

        var up = Assert.Single((await QueryBodyAsync(dataset, store, ascending)).GetProperty("features").EnumerateArray())
            .GetProperty("attributes").GetProperty("p90").GetDouble();
        var down = Assert.Single((await QueryBodyAsync(dataset, store, descending)).GetProperty("features").EnumerateArray())
            .GetProperty("attributes").GetProperty("p90").GetDouble();

        // S3 §percentile example shape: 1..10 at 0.9 ascending is 9, descending is 2.
        Assert.Equal(30.0, up, 9);
        Assert.Equal(10.0, down, 9);
    }

    [Fact]
    public async Task Percentile_over_an_empty_set_yields_null()
    {
        var dataset = Layer();
        var store = new StatsStore(Row(1, "a", 10, 0, 0));
        var query = await ParseAsync(
            ("where", "name = 'Nowhere'"),
            ("outStatistics", """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]"""));

        var body = await QueryBodyAsync(dataset, store, query);

        var attributes = Assert.Single(body.GetProperty("features").EnumerateArray()).GetProperty("attributes");
        Assert.Equal(JsonValueKind.Null, attributes.GetProperty("p50").ValueKind);
    }

    // ---- 2. COUNT DISTINCT: returnCountOnly + returnDistinctValues ----

    [Fact]
    public async Task Count_distinct_shape_parses()
    {
        var query = await ParseAsync(("returnCountOnly", "true"), ("returnDistinctValues", "true"), ("outFields", "name"));

        Assert.True(query.ReturnCountOnly);
        Assert.True(query.ReturnDistinctValues);
    }

    [Fact]
    public async Task Count_distinct_counts_deduplicated_rows()
    {
        var dataset = Layer();
        var store = new StatsStore(
            Row(1, "a", 10, 0, 0), Row(2, "a", 20, 1, 1), Row(3, "b", 30, 2, 2));
        var query = await ParseAsync(("returnCountOnly", "true"), ("returnDistinctValues", "true"), ("outFields", "name"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.Equal(2, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Other_result_shape_clashes_stay_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("returnCountOnly", "true"), ("returnIdsOnly", "true")));
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("returnDistinctValues", "true"), ("returnIdsOnly", "true")));
        const string stats = """[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"}]""";
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("outStatistics", stats), ("returnDistinctValues", "true")));
    }

    // ---- 3. capability-flag honesty on the layer wire shape ----

    private static JsonElement LayerJson()
    {
        var json = JsonSerializer.Serialize(FeatureService.Layer(0, Layer(), editable: false), EsriJson.Options);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Layer_advertises_the_live_statistics_and_search_flags()
    {
        var layer = LayerJson();

        Assert.True(layer.GetProperty("supportsExceedsLimitStatistics").GetBoolean());
        Assert.True(layer.GetProperty("supportsDefaultSR").GetBoolean());
        var caps = layer.GetProperty("advancedQueryCapabilities");
        Assert.True(caps.GetProperty("supportsCountDistinct").GetBoolean());
        Assert.True(caps.GetProperty("supportsPercentileStatistics").GetBoolean());
        Assert.False(caps.GetProperty("supportsFullTextSearch").GetBoolean());
    }

    [Fact]
    public void Layer_lists_no_searchable_fields_while_full_text_stays_off()
    {
        var layer = LayerJson();

        var fields = layer.GetProperty("advancedQueryCapabilities").GetProperty("fullTextSearchableFields");
        Assert.Equal(JsonValueKind.Array, fields.ValueKind);
        Assert.Empty(fields.EnumerateArray());
    }

    // ---- text stays honestly rejected ----

    [Fact]
    public async Task Text_full_text_search_stays_rejected()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("text", "berlin")));

        Assert.Contains("where", exception.Message);
    }

    // ---- cancellation ----

    [Fact]
    public async Task A_cancelled_percentile_query_aborts()
    {
        var dataset = Layer();
        var store = new StatsStore(Row(1, "a", 10, 0, 0));
        var query = await ParseAsync(
            ("outStatistics", """[{"statisticType":"percentile_cont","statisticParameters":{"value":0.5},"onStatisticField":"population","outStatisticFieldName":"p50"}]"""));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await FeatureService.QueryAsync(
            dataset, store, query, Operations, Transforms, cancelled.Token));
    }
}
