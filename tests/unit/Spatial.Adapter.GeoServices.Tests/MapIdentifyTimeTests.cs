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
/// T-059: MapServer identify honours the temporal surface (spec §4.0.5,
/// ADR-0058 root flag). <c>time</c> reuses the query grammar, the documented
/// <c>timeRelation</c> values are accepted (equivalent for the engine's
/// instant date values), and <c>layerTimeOptions</c> carries per-layer
/// opt-out and cumulative display. Red-first: the engine scans without a
/// temporal predicate today, so every filtering test here fails while the
/// parameters are ignored.
/// </summary>
public sealed class MapIdentifyTimeTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();

    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema DatelessSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private const long Instant = 1700000000000;

    private const long Earlier = 1600000000000;

    private static DatasetDescription Cities(FeatureSchema schema) => new(
        "demo.cities", "demo", "cities", "geometry", 4326, "Point", 8, [], schema);

    private static Feature Dated(string name, long milliseconds) => new(
        new FeatureId(name),
        Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.405, 52.52, Crs4326)),
        ]);

    private static Feature Dateless(string name) => new(
        new FeatureId(name),
        DatelessSchema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.405, 52.52, Crs4326)),
        ]);

    private sealed class MemoryStore(Dictionary<string, Feature[]> datasets) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var features = datasets[dataset];
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(features[0].Schema, features)];
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

    private static (string Key, string Value)[] PointParams(params (string Key, string Value)[] extra) =>
    [
        ("geometry", """{"x":13.405,"y":52.52,"spatialReference":{"wkid":4326}}"""),
        ("sr", "4326"),
        ("layers", "all"),
        .. extra,
    ];

    private static IReadOnlyList<MapLayerInfo> Infos(DatasetDescription dataset) =>
    [
        new MapLayerInfo(new PublishedLayer(0, dataset.Id, "Cities"), dataset, Envelope.Empty),
    ];

    private static async Task<JsonElement> IdentifyBodyAsync(
        IFeatureStore store, IReadOnlyList<MapLayerInfo> infos, (string Key, string Value)[] values)
    {
        var result = await MapIdentifyEngine.IdentifyAsync(
            store, infos, await ParamsAsync(values), 4326, Operations, Transforms, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static string[] Names(JsonElement body) =>
        [.. body.GetProperty("results").EnumerateArray().Select(hit => hit.GetProperty("value").GetString()!)];

    [Fact]
    public async Task Time_instant_filters_to_the_containing_date()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant), Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(store, Infos(Cities(Schema)), PointParams(("time", "1700000000000")));

        Assert.Equal(["a"], Names(body));
    }

    [Fact]
    public async Task Absent_time_returns_every_dated_feature()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant), Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(store, Infos(Cities(Schema)), PointParams());

        Assert.Equal(["a", "b"], Names(body));
    }

    [Fact]
    public async Task Time_is_a_no_op_without_date_fields()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dateless("a")] });

        var body = await IdentifyBodyAsync(store, Infos(Cities(DatelessSchema)), PointParams(("time", "1700000000000")));

        Assert.Equal(["a"], Names(body));
    }

    [Fact]
    public async Task Time_extent_with_open_start_matches_early_dates()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant), Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(store, Infos(Cities(Schema)), PointParams(("time", "null,1650000000000")));

        Assert.Equal(["b"], Names(body));
    }

    [Fact]
    public async Task Layer_time_options_opting_out_skips_the_filter()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(
            store, Infos(Cities(Schema)),
            PointParams(("time", "1700000000000"), ("layerTimeOptions", """[{"id":0,"useTime":false}]""")));

        Assert.Equal(["b"], Names(body));
    }

    [Fact]
    public async Task Cumulative_layer_shows_data_up_to_the_end()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(
            store, Infos(Cities(Schema)),
            PointParams(("time", "1650000000000,1750000000000"), ("layerTimeOptions", """[{"id":0,"timeDataCumulative":true}]""")));

        Assert.Equal(["b"], Names(body));
    }

    [Theory]
    [InlineData("esriTimeRelationOverlaps")]
    [InlineData("esriTimeRelationContains")]
    [InlineData("esriTimeRelationWithin")]
    public async Task Documented_time_relations_filter_like_the_default(string relation)
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant), Dated("b", Earlier)] });

        var body = await IdentifyBodyAsync(
            store, Infos(Cities(Schema)), PointParams(("time", "1700000000000"), ("timeRelation", relation)));

        Assert.Equal(["a"], Names(body));
    }

    [Fact]
    public async Task A_malformed_time_is_a_typed_error()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant)] });
        var parameters = await ParamsAsync(PointParams(("time", "yesterday")));

        var failure = await Assert.ThrowsAsync<EsriInteropException>(() =>
            MapIdentifyEngine.IdentifyAsync(
                store, Infos(Cities(Schema)), parameters,
                4326, Operations, Transforms, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public async Task An_unknown_time_relation_is_a_typed_error()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant)] });
        var parameters = await ParamsAsync(PointParams(("time", "1700000000000"), ("timeRelation", "esriTimeRelationFoo")));

        var failure = await Assert.ThrowsAsync<EsriInteropException>(() =>
            MapIdentifyEngine.IdentifyAsync(
                store, Infos(Cities(Schema)), parameters,
                4326, Operations, Transforms, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public async Task Malformed_layer_time_options_are_a_typed_error()
    {
        var store = new MemoryStore(new Dictionary<string, Feature[]> { ["demo.cities"] = [Dated("a", Instant)] });
        var parameters = await ParamsAsync(PointParams(("layerTimeOptions", "not-json")));

        var failure = await Assert.ThrowsAsync<EsriInteropException>(() =>
            MapIdentifyEngine.IdentifyAsync(
                store, Infos(Cities(Schema)), parameters,
                4326, Operations, Transforms, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }
}
