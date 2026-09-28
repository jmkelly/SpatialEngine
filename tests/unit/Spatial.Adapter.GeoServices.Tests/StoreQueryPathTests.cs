using System.Globalization;
using System.Text.Json;
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
/// The served query compiled onto the store's query surface (ADR-0075 §7):
/// the count, the distinct set and the paged feature read are asked of the
/// store's own face instead of being computed over a materialised match set,
/// and a request the plan cannot carry keeps the match path. Each test asserts
/// both halves: the store was asked, and the JSON is the one the match path
/// would have written.
/// </summary>
public sealed class StoreQueryPathTests
{
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
        "demo.places", "demo", "places", "geometry", 4326, "Point", 3, ["id"], Schema);

    private static Feature Row(long id, string name, long population, double x, double y) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(population),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
        ]);

    private static IReadOnlyList<Feature> Rows { get; } =
    [
        Row(1, "Berlin", 100, 13.4, 52.5),
        Row(2, "Paris", 200, 2.35, 48.85),
        Row(3, "Rome", 300, 12.5, 41.9),
    ];

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, Crs4326);
    }

    private static async Task<JsonElement> BodyAsync(DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
    {
        var result = await FeatureService.QueryAsync(dataset, store, query, Operations, Relations, Transforms, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Return_count_only_is_asked_of_the_store_and_the_dataset_is_never_scanned()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(Layer(), store, await ParseAsync(("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(3, body.GetProperty("count").GetInt32());
        Assert.Equal(1, store.Counts);
        Assert.Equal(0, store.Scans);
    }

    [Fact]
    public async Task Count_distinct_is_asked_of_the_store()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(
            Layer(), store, await ParseAsync(("returnCountOnly", "true"), ("returnDistinctValues", "true"), ("f", "json")));

        Assert.Equal(3, body.GetProperty("count").GetInt32());
        Assert.Equal(1, store.Distincts);
        Assert.Equal(0, store.Scans);
    }

    [Fact]
    public async Task Distinct_values_are_asked_of_the_store()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(Layer(), store, await ParseAsync(("returnDistinctValues", "true"), ("f", "json")));

        Assert.Equal(3, body.GetProperty("features").GetArrayLength());
        Assert.Equal(1, store.Distincts);
        Assert.Equal(0, store.Scans);
    }

    [Fact]
    public async Task A_paged_ordered_projected_read_is_asked_of_the_store()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(
            Layer(),
            store,
            await ParseAsync(
                ("orderByFields", "population DESC"),
                ("outFields", "name"),
                ("resultRecordCount", "2"),
                ("f", "json")));

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        Assert.Equal(["population"], Assert.IsAssignableFrom<IReadOnlyList<OrderTerm>>(plan.Order)!.Select(term => term.Field));
        Assert.Equal(SortDirection.Descending, plan.Order![0].Direction);
        Assert.Equal(["name", "geometry", "id"], plan.Projection);
        Assert.Equal(2, plan.Limit);
        Assert.Equal(0, store.Scans);
        Assert.Equal(2, body.GetProperty("features").GetArrayLength());
        Assert.Equal("Rome", body.GetProperty("features")[0].GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal("Paris", body.GetProperty("features")[1].GetProperty("attributes").GetProperty("name").GetString());
        Assert.True(body.GetProperty("exceededTransferLimit").GetBoolean());
    }

    /// <summary>
    /// The where clause is the plan's predicate (ADR-0074 §7, ADR-0083): on a
    /// layer whose <c>OBJECTID</c> is store-derived the store counts the
    /// matching rows itself, and the scan-and-match path is not taken.
    /// </summary>
    [Fact]
    public async Task A_where_clause_is_pushed_and_counted_by_the_store()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(
            Layer(), store, await ParseAsync(("where", "population > 150"), ("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(1, store.Counts);
        Assert.Equal(0, store.Scans);
        Assert.NotNull(store.LastPlan?.Where);
    }

    /// <summary>
    /// The same clause on a layer whose <c>OBJECTID</c> is the scan ordinal is
    /// <em>not</em> a pushdown: a store returning only the matching rows would
    /// renumber that key, so the same feature would come back with an object id
    /// that depends on the query (ADR-0083). The facade keeps the clause and
    /// counts the matches itself.
    /// </summary>
    [Fact]
    public async Task A_where_clause_the_plan_cannot_carry_keeps_the_match_path()
    {
        var store = new PushingStore(Rows);
        var layer = new DatasetDescription("demo.places", "demo", "places", "geometry", 4326, "Point", 3, [], Schema);
        var body = await BodyAsync(
            layer, store, await ParseAsync(("where", "population > 150"), ("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(0, store.Counts);
        Assert.Equal(1, store.Scans);
    }

    [Fact]
    public async Task A_topological_spatial_relation_keeps_the_match_path()
    {
        var store = new PushingStore(Rows);
        var body = await BodyAsync(
            Layer(),
            store,
            await ParseAsync(("geometry", "-1,-1,1,1"), ("spatialRel", "esriSpatialRelContains"), ("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(0, store.Counts);
        Assert.Equal(1, store.Scans);
    }

    [Fact]
    public async Task The_envelope_pre_filter_is_pushed_as_the_plan_box()
    {
        var store = new PushingStore(Rows);
        await BodyAsync(
            Layer(), store, await ParseAsync(("geometry", "0,0,3,50"), ("spatialRel", "esriSpatialRelEnvelopeIntersects"), ("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(1, store.Counts);
        Assert.NotNull(store.LastPlan?.BoundingBox);
    }

    [Fact]
    public async Task A_layer_without_a_durable_object_id_keeps_the_feature_match_path_but_still_pushes_the_count()
    {
        var store = new PushingStore(Rows);
        var layer = new DatasetDescription("demo.places", "demo", "places", "geometry", 4326, "Point", 3, [], Schema);

        var features = await BodyAsync(layer, store, await ParseAsync(("outFields", "name"), ("f", "json")));
        var count = await BodyAsync(layer, store, await ParseAsync(("returnCountOnly", "true"), ("f", "json")));

        Assert.Equal(3, features.GetProperty("features").GetArrayLength());
        Assert.Equal(1, store.Scans);
        Assert.Equal(3, count.GetProperty("count").GetInt32());
        Assert.Equal(1, store.Counts);
    }

    [Fact]
    public async Task A_cancelled_request_cancels_rather_than_answers()
    {
        var store = new PushingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var query = await ParseAsync(("returnCountOnly", "true"), ("f", "json"));
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureService.QueryAsync(Layer(), store, query, Operations, Relations, Transforms, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    /// <summary>
    /// A store with the whole query surface: it records what it was asked and
    /// answers with the shared reference executor, which is exactly what a
    /// pushing provider must reproduce.
    /// </summary>
    private sealed class PushingStore(IReadOnlyList<Feature> features) : IFeatureStore, IFeatureAggregateStore
    {
        public int Scans { get; private set; }

        public int Counts { get; private set; }

        public int Distincts { get; private set; }

        public FeatureQuery? LastPlan { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPlan = query;
            return Task.FromResult(FeaturePlanExecutor.Execute(Schema, features, query, cancellationToken));
        }

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Counts++;
            LastPlan = query;
            return Task.FromResult(FeatureReduction.CountFeatures(FeaturePlanExecutor.Select(Schema, features, query, cancellationToken)));
        }

        public Task<DistinctPage> DistinctAsync(
            string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Distincts++;
            LastPlan = query;
            return Task.FromResult(
                FeatureReduction.Distinct(Schema, FeaturePlanExecutor.Select(Schema, features, query, cancellationToken), distinct));
        }

        public Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                FeatureReduction.Aggregate(Schema, FeaturePlanExecutor.Select(Schema, features, query, cancellationToken), aggregate));


        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
