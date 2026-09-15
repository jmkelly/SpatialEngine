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
/// T-038 item 1: the service-level <c>FeatureServer/query</c> (S1
/// query-feature-service/). Red-first: only layer-level
/// <c>.../&lt;id&gt;/query</c> is served today, and there is no
/// <c>layerDefs</c> parser, so every test here fails while the surface is
/// unmounted.
/// </summary>
public sealed class FeatureServiceQueryTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();

    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription Cities() => new(
        "demo.cities", "demo", "cities", "geometry", 4326, "Point", 8, [], Schema);

    private static DatasetDescription Towns() => new(
        "demo.towns", "demo", "towns", "geometry", 4326, "Point", 2, [], Schema);

    private static Feature Row(string name, long population, double x, double y) => new(
        new FeatureId(name),
        Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(population),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private sealed class ServiceStore : IFeatureStore
    {
        private readonly Dictionary<string, Feature[]> _datasets;

        public ServiceStore(Dictionary<string, Feature[]> datasets) => _datasets = datasets;

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, _datasets[dataset])];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values) =>
        EsriFeatureQuery.Parse(await ParamsAsync(values), Crs4326);

    private static async Task<JsonElement> ServiceBodyAsync(
        IReadOnlyList<ServiceLayerQuery> layers,
        IFeatureStore store,
        EsriFeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await FeatureResponseWriter.ServiceQueryAsync(layers, store, query, Operations, Transforms, cancellationToken);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static ServiceLayerQuery Layer(int id, DatasetDescription description, EsriFeatureQuery query) =>
        new(id, description, query, EsriLayerModel.IsTable(description));

    // ---- layerDefs parsing (S1: simple, JSON object, JSON array) ----

    [Fact]
    public void An_absent_layer_defs_selects_every_layer()
    {
        var defs = FeatureServiceQuery.ParseLayerDefs(null);

        Assert.Empty(defs);
    }

    [Fact]
    public void The_simple_layer_defs_syntax_parses_per_layer_where_clauses()
    {
        var defs = FeatureServiceQuery.ParseLayerDefs("0:POP2000 > 1000000;5:AREA > 100000");

        Assert.Equal(2, defs.Count);
        Assert.Equal("POP2000 > 1000000", defs[0].Where?.ToWhere());
        Assert.Equal("AREA > 100000", defs[5].Where?.ToWhere());
    }

    [Fact]
    public void The_json_object_layer_defs_syntax_parses()
    {
        var defs = FeatureServiceQuery.ParseLayerDefs("""{"0": "POP2000 > 1000000", "5": "AREA > 100000"}""");

        Assert.Equal(2, defs.Count);
        Assert.NotNull(defs[0].Where);
        Assert.NotNull(defs[5].Where);
    }

    [Fact]
    public void The_json_array_layer_defs_syntax_parses_where_and_out_fields()
    {
        var defs = FeatureServiceQuery.ParseLayerDefs(
            """[{"layerId": 0, "where": "OBJECTID < 100", "outFields": "OBJECTID,name"}, {"layerId": 1, "where": "OBJECTID < 323"}]""");

        Assert.Equal(2, defs.Count);
        Assert.Equal(["OBJECTID", "name"], defs[0].OutFields);
        Assert.NotNull(defs[0].Where);
        Assert.NotNull(defs[1].Where);
        Assert.Null(defs[1].OutFields);
    }

    [Fact]
    public void A_layer_defs_where_outside_the_closed_grammar_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() =>
            FeatureServiceQuery.ParseLayerDefs("""{"0": "POP2000 > FUNC(1)"}"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public void A_layer_defs_entry_without_a_layer_id_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() =>
            FeatureServiceQuery.ParseLayerDefs("""[{"where": "OBJECTID < 100"}]"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    // ---- per-layer query combination ----

    [Fact]
    public async Task A_layer_defs_where_narrows_the_shared_where()
    {
        var query = await ParseAsync(("where", "population > 100"));
        var defs = FeatureServiceQuery.ParseLayerDefs("""{"0": "population < 10000000"}""");

        var layered = FeatureServiceQuery.ForLayer(query, defs[0]);

        Assert.NotNull(layered.Where);
        Assert.Contains("population > 100", layered.Where.ToWhere(), StringComparison.Ordinal);
        Assert.Contains("population < 10000000", layered.Where.ToWhere(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layer_defs_out_fields_override_out_fields_for_that_layer_only()
    {
        var query = await ParseAsync(("outFields", "name"));
        var defs = FeatureServiceQuery.ParseLayerDefs(
            """[{"layerId": 0, "outFields": "population"}]""");

        Assert.Equal(["population"], FeatureServiceQuery.ForLayer(query, defs[0]).OutFields);
        Assert.Equal(["name"], FeatureServiceQuery.ForLayer(query, null).OutFields);
    }

    // ---- service execution across layers ----

    [Fact]
    public async Task The_service_query_returns_one_feature_set_per_layer()
    {
        var store = new ServiceStore(new Dictionary<string, Feature[]>
        {
            ["demo.cities"] = [Row("berlin", 3664000, 13.4, 52.5)],
            ["demo.towns"] = [Row("eythorne", 2500, 1.2, 51.1)],
        });
        var query = await ParseAsync(("where", "1=1"));

        var body = await ServiceBodyAsync(
            [Layer(0, Cities(), query), Layer(1, Towns(), query)], store, query);

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(2, layers.Length);
        Assert.Equal(0, layers[0].GetProperty("id").GetInt32());
        Assert.Equal("OBJECTID", layers[0].GetProperty("objectIdFieldName").GetString());
        Assert.Equal("esriGeometryPoint", layers[0].GetProperty("geometryType").GetString());
        Assert.Single(layers[0].GetProperty("features").EnumerateArray());
        Assert.Equal(1, layers[1].GetProperty("id").GetInt32());
        Assert.Single(layers[1].GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task The_service_query_count_shape_counts_each_layer()
    {
        var store = new ServiceStore(new Dictionary<string, Feature[]>
        {
            ["demo.cities"] = [Row("berlin", 3664000, 13.4, 52.5), Row("paris", 2150000, 2.3, 48.8)],
            ["demo.towns"] = [Row("eythorne", 2500, 1.2, 51.1)],
        });
        var query = await ParseAsync(("returnCountOnly", "true"));

        var body = await ServiceBodyAsync(
            [Layer(0, Cities(), query), Layer(1, Towns(), query)], store, query);

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(2, layers[0].GetProperty("count").GetInt32());
        Assert.Equal(1, layers[1].GetProperty("count").GetInt32());
        Assert.False(layers[0].TryGetProperty("features", out _));
    }

    [Fact]
    public async Task The_service_query_ids_shape_lists_ids_per_layer()
    {
        var store = new ServiceStore(new Dictionary<string, Feature[]>
        {
            ["demo.cities"] = [Row("berlin", 3664000, 13.4, 52.5)],
            ["demo.towns"] = [Row("eythorne", 2500, 1.2, 51.1)],
        });
        var query = await ParseAsync(("returnIdsOnly", "true"));

        var body = await ServiceBodyAsync(
            [Layer(0, Cities(), query), Layer(1, Towns(), query)], store, query);

        var layers = body.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal([1L], layers[0].GetProperty("objectIds").EnumerateArray().Select(id => id.GetInt64()).ToArray());
        Assert.Equal([1L], layers[1].GetProperty("objectIds").EnumerateArray().Select(id => id.GetInt64()).ToArray());
    }

    [Fact]
    public async Task Layer_level_result_shapes_are_rejected_with_the_layer_query_named()
    {
        var store = new ServiceStore(new Dictionary<string, Feature[]>
        {
            ["demo.cities"] = [Row("berlin", 3664000, 13.4, 52.5)],
        });
        var query = await ParseAsync(("returnExtentOnly", "true"));

        var exception = await Assert.ThrowsAsync<EsriInteropException>(async () => await ServiceBodyAsync(
            [Layer(0, Cities(), query)], store, query));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("returnExtentOnly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_service_query_aborts()
    {
        var store = new ServiceStore(new Dictionary<string, Feature[]>
        {
            ["demo.cities"] = [Row("berlin", 3664000, 13.4, 52.5)],
        });
        var query = await ParseAsync(("where", "1=1"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await ServiceBodyAsync(
            [Layer(0, Cities(), query)], store, query, cancelled.Token));
    }
}
