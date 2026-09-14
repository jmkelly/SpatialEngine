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
/// T-036: the Feature modern query params (research/compat/feature-service.md
/// §1, S3 11.3–11.5): <c>returnEnvelope</c> (envelope geometries, 11.4+),
/// <c>defaultSR</c> (request-wide SR shorthand, 11.3+),
/// <c>resultPaginationToken</c> (keyset paging workflow, S3) and
/// <c>uniqueIds</c>/<c>returnUniqueIdsOnly</c> (string-ID databases, 11.5+).
/// Red-first replays at the response level: every param is silently ignored
/// today, so each test fails against the wire shape until the fix lands.
/// </summary>
public sealed class EsriModernQueryParamsTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();

    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema IntSchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema StringSchema = new(
    [
        new FieldDefinition("guid", AttributeKind.String),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription IntLayer(int srid = 4326) => new(
        "demo.places", "demo", "places", "geometry", srid, "Point", 5, ["id"], IntSchema);

    private static DatasetDescription StringLayer() => new(
        "demo.devices", "demo", "devices", "geometry", 4326, "Point", 3, ["guid"], StringSchema);

    private static Feature IntRow(long id, string name, double x, double y) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        IntSchema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private static Feature StringRow(string guid, string name, double x, double y) => new(
        new FeatureId(guid),
        StringSchema,
        [
            AttributeValue.FromString(guid),
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> ParseAsync(DatasetDescription dataset, params (string Key, string Value)[] values) =>
        EsriFeatureQuery.Parse(await ParamsAsync(values), EsriLayerModel.LayerCoordinateReference(dataset.Srid));

    private static async Task<JsonElement> QueryBodyAsync(DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
    {
        var result = await FeatureService.QueryAsync(dataset, store, query, Operations, Transforms, CancellationToken.None);
        return await BodyAsync(result);
    }

    private static async Task<JsonElement> BodyAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class QueryStore : IFeatureStore
    {
        private readonly Feature[] _features;

        public QueryStore(params Feature[] features) => _features = features;

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = _features.Length > 0 ? _features[0].Schema : IntSchema;
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(schema, _features)];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    // ---- 1. returnEnvelope (S3 11.4+): envelopes instead of geometries ----

    [Fact]
    public async Task Return_envelope_writes_envelope_geometries()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 10, 20), IntRow(2, "b", 30, 40));
        var query = await ParseAsync(dataset, ("where", "1=1"), ("returnEnvelope", "true"));

        var body = await QueryBodyAsync(dataset, store, query);

        var features = body.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(2, features.Length);
        var geometry = features[0].GetProperty("geometry");
        Assert.Equal(10.0, geometry.GetProperty("xmin").GetDouble());
        Assert.Equal(20.0, geometry.GetProperty("ymin").GetDouble());
        Assert.Equal(10.0, geometry.GetProperty("xmax").GetDouble());
        Assert.Equal(20.0, geometry.GetProperty("ymax").GetDouble());
    }

    [Fact]
    public async Task Without_return_envelope_geometries_stay_full()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 10, 20));
        var query = await ParseAsync(dataset, ("where", "1=1"));

        var body = await QueryBodyAsync(dataset, store, query);

        var geometry = body.GetProperty("features").EnumerateArray().First().GetProperty("geometry");
        Assert.Equal(10.0, geometry.GetProperty("x").GetDouble());
        Assert.Equal(20.0, geometry.GetProperty("y").GetDouble());
    }

    // ---- 2. defaultSR (S3 11.3+): request-wide SR shorthand ----

    [Fact]
    public async Task Default_sr_sets_the_response_spatial_reference()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 16.37, 48.20));
        var query = await ParseAsync(dataset, ("where", "1=1"), ("defaultSR", "3857"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.Equal(3857, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task Out_sr_wins_over_default_sr()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 16.37, 48.20));
        var query = await ParseAsync(dataset, ("where", "1=1"), ("defaultSR", "3857"), ("outSR", "4326"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.Equal(4326, body.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    // ---- 3. resultPaginationToken (S3 keyset paging workflow) ----

    [Fact]
    public async Task A_full_page_returns_a_pagination_token()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0), IntRow(2, "b", 1, 1), IntRow(3, "c", 2, 2));
        var query = await ParseAsync(dataset, ("where", "1=1"), ("orderByFields", "OBJECTID"), ("resultRecordCount", "2"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.True(body.GetProperty("exceededTransferLimit").GetBoolean());
        var token = body.GetProperty("resultPaginationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(2, body.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task The_pagination_token_continues_the_page_sequence()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0), IntRow(2, "b", 1, 1), IntRow(3, "c", 2, 2));
        var first = await QueryBodyAsync(
            dataset, store,
            await ParseAsync(dataset, ("where", "1=1"), ("orderByFields", "OBJECTID"), ("resultRecordCount", "2")));
        var token = first.GetProperty("resultPaginationToken").GetString()!;

        var second = await QueryBodyAsync(
            dataset, store,
            await ParseAsync(dataset, ("where", "1=1"), ("orderByFields", "OBJECTID"), ("resultRecordCount", "2"), ("resultPaginationToken", token)));

        var feature = Assert.Single(second.GetProperty("features").EnumerateArray());
        Assert.Equal(3, feature.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.False(second.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task A_bogus_pagination_token_is_rejected()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0));

        await Assert.ThrowsAsync<EsriInteropException>(async () => await QueryBodyAsync(
            dataset, store,
            EsriFeatureQuery.Parse(
                await ParamsAsync(("where", "1=1"), ("resultPaginationToken", "!!not-a-token!!")),
                EsriLayerModel.LayerCoordinateReference(dataset.Srid))));
    }

    [Fact]
    public async Task Pagination_token_and_offset_are_mutually_exclusive()
    {
        var dataset = IntLayer();

        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            dataset, ("where", "1=1"), ("resultOffset", "2"), ("resultPaginationToken", "e30")));
    }

    // ---- 4. uniqueIds / returnUniqueIdsOnly (S3 11.5+, string-ID databases) ----

    [Fact]
    public async Task Unique_ids_filter_a_string_identity_layer()
    {
        var dataset = StringLayer();
        var store = new QueryStore(StringRow("a", "x", 0, 0), StringRow("b", "y", 1, 1), StringRow("c", "z", 2, 2));
        var query = await ParseAsync(dataset, ("uniqueIds", "a,c"));

        var body = await QueryBodyAsync(dataset, store, query);

        var guids = body.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("guid").GetString()!)
            .ToArray();
        Assert.Equal(["a", "c"], guids);
    }

    [Fact]
    public async Task Return_unique_ids_only_returns_the_string_id_set()
    {
        var dataset = StringLayer();
        var store = new QueryStore(StringRow("a", "x", 0, 0), StringRow("b", "y", 1, 1));
        var query = await ParseAsync(dataset, ("returnUniqueIdsOnly", "true"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.Equal("guid", body.GetProperty("uniqueIdFieldName").GetString());
        var ids = body.GetProperty("uniqueIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Equal(["a", "b"], ids);
    }

    [Fact]
    public async Task Unique_ids_on_an_integer_layer_are_rejected_by_name()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0));

        var exception = await Assert.ThrowsAsync<EsriInteropException>(async () => await QueryBodyAsync(
            dataset, store, await ParseAsync(dataset, ("uniqueIds", "1"))));

        Assert.Contains("uniqueIds", exception.Message);
    }

    [Fact]
    public async Task Return_unique_ids_only_on_an_integer_layer_is_rejected_by_name()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0));

        var exception = await Assert.ThrowsAsync<EsriInteropException>(async () => await QueryBodyAsync(
            dataset, store, await ParseAsync(dataset, ("returnUniqueIdsOnly", "true"))));

        Assert.Contains("returnUniqueIdsOnly", exception.Message);
    }

    // ---- Parse surface: defaults, precedence, exclusivity ----

    [Fact]
    public async Task Modern_params_default_to_absent()
    {
        var query = await ParseAsync(IntLayer(), ("where", "1=1"));

        Assert.False(query.ReturnEnvelope);
        Assert.Null(query.DefaultSr);
        Assert.Null(query.ResultPaginationToken);
        Assert.Null(query.UniqueIds);
        Assert.False(query.ReturnUniqueIdsOnly);
    }

    [Fact]
    public async Task Return_envelope_parses()
    {
        Assert.True((await ParseAsync(IntLayer(), ("returnEnvelope", "true"))).ReturnEnvelope);
        Assert.True((await ParseAsync(IntLayer(), ("returnEnvelope", "1"))).ReturnEnvelope);
    }

    [Fact]
    public async Task Default_sr_parses_and_backs_the_output_reference()
    {
        var query = await ParseAsync(IntLayer(), ("defaultSR", "3857"));

        Assert.Equal(CoordinateReference.Epsg(3857), query.DefaultSr);
        Assert.Equal(CoordinateReference.Epsg(3857), query.OutSr);
    }

    [Fact]
    public async Task Default_sr_backs_input_geometries_after_in_sr()
    {
        var dataset = IntLayer();
        var fallback = EsriLayerModel.LayerCoordinateReference(dataset.Srid);

        var fromDefault = EsriFeatureQuery.Parse(
            await ParamsAsync(("geometry", "{\"x\":1,\"y\":2}"), ("defaultSR", "3857")), fallback);
        Assert.Equal(CoordinateReference.Epsg(3857), fromDefault.Geometry!.CoordinateReference);

        var inSrWins = EsriFeatureQuery.Parse(
            await ParamsAsync(("geometry", "{\"x\":1,\"y\":2}"), ("defaultSR", "3857"), ("inSR", "4326")), fallback);
        Assert.Equal(CoordinateReference.Epsg(4326), inSrWins.Geometry!.CoordinateReference);

        var layerFallback = EsriFeatureQuery.Parse(await ParamsAsync(("geometry", "{\"x\":1,\"y\":2}")), fallback);
        Assert.Equal(CoordinateReference.Epsg(4326), layerFallback.Geometry!.CoordinateReference);
    }

    [Fact]
    public async Task A_malformed_default_sr_is_rejected()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(IntLayer(), ("defaultSR", "bogus")));
    }

    [Fact]
    public async Task Pagination_token_and_offset_name_each_other()
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            IntLayer(), ("resultOffset", "2"), ("resultPaginationToken", "e30")));

        Assert.Contains("resultOffset", exception.Message);
        Assert.Contains("resultPaginationToken", exception.Message);
    }

    [Theory]
    [InlineData("returnIdsOnly")]
    [InlineData("returnCountOnly")]
    [InlineData("returnExtentOnly")]
    [InlineData("returnUniqueIdsOnly")]
    public async Task Pagination_token_rejects_unpaged_shapes(string shape)
    {
        var exception = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            IntLayer(), (shape, "true"), ("resultPaginationToken", "e30")));

        Assert.Contains(shape, exception.Message);
    }

    [Fact]
    public async Task Unique_ids_parse_as_strings()
    {
        var query = await ParseAsync(StringLayer(), ("uniqueIds", "a, b ,c"));

        Assert.Equal(["a", "b", "c"], query.UniqueIds);
    }

    [Fact]
    public async Task Unique_ids_compose_with_object_ids()
    {
        var query = await ParseAsync(StringLayer(), ("uniqueIds", "a"), ("objectIds", "1"));

        Assert.Equal(["a"], query.UniqueIds);
        Assert.Equal([1L], query.ObjectIds);
    }

    [Fact]
    public async Task Return_unique_ids_only_is_a_result_shape()
    {
        Assert.True((await ParseAsync(StringLayer(), ("returnUniqueIdsOnly", "true"))).ReturnUniqueIdsOnly);
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            StringLayer(), ("returnUniqueIdsOnly", "true"), ("returnCountOnly", "true")));
        const string stats = "[{\"statisticType\":\"count\",\"onStatisticField\":\"*\",\"outStatisticFieldName\":\"n\"}]";
        await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(
            StringLayer(), ("returnUniqueIdsOnly", "true"), ("outStatistics", stats)));
    }

    [Fact]
    public async Task Return_envelope_without_geometry_output_writes_no_geometry()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 10, 20));
        var query = await ParseAsync(dataset, ("returnEnvelope", "true"), ("returnGeometry", "false"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.False(body.GetProperty("features").EnumerateArray().First().TryGetProperty("geometry", out _));
    }

    // ---- Cancellation: long-running work stays a cancellable Task ----

    [Fact]
    public async Task A_cancelled_query_aborts()
    {
        var dataset = IntLayer();
        var store = new QueryStore(IntRow(1, "a", 0, 0));
        var query = await ParseAsync(dataset, ("where", "1=1"), ("returnEnvelope", "true"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await FeatureService.QueryAsync(
            dataset, store, query, Operations, Transforms, cancelled.Token));
    }
}
