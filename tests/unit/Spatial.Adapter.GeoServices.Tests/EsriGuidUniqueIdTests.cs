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
/// T-058: datasets with a single <see cref="AttributeKind.Guid"/> identity
/// column serve <c>uniqueIds</c>/<c>returnUniqueIdsOnly</c> with a documented
/// canonical string form (lowercase <c>D</c>, round-trippable through
/// <see cref="FeatureId"/>), instead of the T-036 honest reject-by-name.
/// The reject still stands for layers with no string-or-guid identity.
/// </summary>
public sealed class EsriGuidUniqueIdTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly NtsGeometryOperations Operations = new();

    private static readonly ProjNetTransforms Transforms = new();

    private static readonly Guid IdA = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private static readonly Guid IdB = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    private static readonly FeatureSchema GuidSchema = new(
    [
        new FieldDefinition("assetId", AttributeKind.Guid),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema NullableGuidSchema = new(
    [
        new FieldDefinition("assetId", AttributeKind.Guid, nullable: true),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription GuidLayer() => new(
        "demo.assets", "demo", "assets", "geometry", 4326, "Point", 3, ["assetId"], GuidSchema);

    private static DatasetDescription NullableGuidLayer() => new(
        "demo.assets", "demo", "assets", "geometry", 4326, "Point", 3, ["assetId"], NullableGuidSchema);

    private static Feature GuidRow(Guid id, string name, double x, double y, FeatureSchema? schema = null)
    {
        schema ??= GuidSchema;
        return new Feature(
            new FeatureId(id.ToString("D")),
            schema,
            [
                AttributeValue.FromGuid(id),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, Crs4326)),
            ]);
    }

    private static Feature NullGuidRow(string name, double x, double y) => new(
        new FeatureId("null-row"),
        NullableGuidSchema,
        [
            AttributeValue.Null,
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
            var schema = _features.Length > 0 ? _features[0].Schema : GuidSchema;
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(schema, _features)];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    // ---- Scheme: guid identity columns have a unique-id model ----

    [Fact]
    public void For_accepts_a_single_guid_identity_column()
    {
        var scheme = EsriUniqueIdScheme.For(GuidLayer());

        Assert.NotNull(scheme);
        Assert.Equal("assetId", scheme.FieldName);
    }

    [Fact]
    public void TryResolve_emits_the_canonical_lowercase_form()
    {
        var scheme = EsriUniqueIdScheme.For(GuidLayer());
        Assert.NotNull(scheme);

        Assert.True(scheme.TryResolve(GuidRow(IdA, "a", 0, 0), out var uniqueId));
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", uniqueId);
    }

    [Fact]
    public void Canonical_form_round_trips_through_feature_id()
    {
        var scheme = EsriUniqueIdScheme.For(GuidLayer());
        Assert.NotNull(scheme);
        Assert.True(scheme.TryResolve(GuidRow(IdA, "a", 0, 0), out var uniqueId));

        var roundTripped = Guid.Parse(new FeatureId(uniqueId).Value).ToString("D");
        Assert.Equal(uniqueId, roundTripped);
    }

    [Fact]
    public void TryResolve_rejects_a_null_guid()
    {
        var scheme = EsriUniqueIdScheme.For(NullableGuidLayer());
        Assert.NotNull(scheme);

        Assert.False(scheme.TryResolve(NullGuidRow("a", 0, 0), out _));
    }

    // ---- Success: guid layers serve uniqueIds ----

    [Fact]
    public async Task Unique_ids_filter_a_guid_identity_layer()
    {
        var dataset = GuidLayer();
        var store = new QueryStore(GuidRow(IdA, "x", 0, 0), GuidRow(IdB, "y", 1, 1));
        var query = await ParseAsync(dataset, ("uniqueIds", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

        var body = await QueryBodyAsync(dataset, store, query);

        var feature = Assert.Single(body.GetProperty("features").EnumerateArray());
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", feature.GetProperty("attributes").GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task Return_unique_ids_only_returns_the_canonical_guid_set()
    {
        var dataset = GuidLayer();
        var store = new QueryStore(GuidRow(IdA, "x", 0, 0), GuidRow(IdB, "y", 1, 1));
        var query = await ParseAsync(dataset, ("returnUniqueIdsOnly", "true"));

        var body = await QueryBodyAsync(dataset, store, query);

        Assert.Equal("assetId", body.GetProperty("uniqueIdFieldName").GetString());
        var ids = body.GetProperty("uniqueIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Equal(["3f2504e0-4f89-11d3-9a0c-0305e82c3301", "6ba7b810-9dad-11d1-80b4-00c04fd430c8"], ids);
    }

    [Fact]
    public void Describe_advertises_the_guid_unique_id_field()
    {
        var layer = EsriLayerModel.Describe(0, GuidLayer(), editable: false);

        Assert.NotNull(layer.UniqueIdField);
        Assert.Equal("assetId", layer.UniqueIdField.Name);
        Assert.False(layer.UniqueIdField.IsSystemMaintained);
    }

    // ---- Failure: a null guid is a typed server failure ----

    [Fact]
    public async Task Return_unique_ids_only_with_a_null_guid_fails()
    {
        var dataset = NullableGuidLayer();
        var store = new QueryStore(NullGuidRow("a", 0, 0));
        var query = await ParseAsync(dataset, ("returnUniqueIdsOnly", "true"));

        var exception = await Assert.ThrowsAsync<EsriInteropException>(async () => await QueryBodyAsync(dataset, store, query));

        Assert.Equal(EsriErrorCodes.ServerError, exception.Code);
    }

    [Fact]
    public async Task Unique_ids_with_a_null_guid_fails()
    {
        var dataset = NullableGuidLayer();
        var store = new QueryStore(NullGuidRow("a", 0, 0));
        var query = await ParseAsync(dataset, ("uniqueIds", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

        var exception = await Assert.ThrowsAsync<EsriInteropException>(async () => await QueryBodyAsync(dataset, store, query));

        Assert.Equal(EsriErrorCodes.ServerError, exception.Code);
    }

    // ---- Cancellation: long-running work stays a cancellable Task ----

    [Fact]
    public async Task A_cancelled_guid_unique_ids_query_aborts()
    {
        var dataset = GuidLayer();
        var store = new QueryStore(GuidRow(IdA, "a", 0, 0));
        var query = await ParseAsync(dataset, ("returnUniqueIdsOnly", "true"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await FeatureService.QueryAsync(
            dataset, store, query, Operations, Transforms, cancelled.Token));
    }
}
