using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>queryRelatedRecords</c> traversal (spec §9.1.5, ADR-0077) driven
/// directly over fakes: the served shape, the related layer's own filter and
/// projection, and cancellation. The fakes deliberately ignore their
/// <see cref="CancellationToken"/>, so a cancelled token must still abort the
/// traversal — long-running work is a cancellable <see cref="Task"/>
/// (AGENTS.md hard wall). The served routes and the write gate are proved
/// end-to-end in the host suite.
/// </summary>
public sealed class FeatureRelationshipEngineTests
{
    private static readonly FeatureSchema ParentSchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema ChildSchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("parent_id", AttributeKind.Int64, nullable: true),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly DatasetDescription Parents = new(
        "test.parents", "test", "parents", "geometry", 4326, "Point", 2, ["id"], ParentSchema);

    private static readonly DatasetDescription Children = new(
        "test.children", "test", "children", "geometry", 4326, "Point", 3, ["id"], ChildSchema);

    private static readonly LayerRelationship Declared = new("children", 1, "id", "parent_id");

    private static QueryRequest Request(string query, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.QueryString = new QueryString($"?{query}");
        return new QueryRequest(
            new GeoServicesCatalog(new GeoServicesOptions()),
            new StubRegistry(),
            "test",
            context,
            new StubRegistry(),
            cancellationToken);
    }

    private static Task<JsonElement> ExecuteAsync(string query, CancellationToken cancellationToken = default) =>
        Body(FeatureRelationshipEngine.QueryRelatedRecordsAsync(
            Request(query, cancellationToken), 0, null!, null!, null!, cancellationToken));

    private static async Task<JsonElement> Body(Task<IResult> pending)
    {
        var result = await pending;
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static readonly string[] MitteAndKreuzberg = ["Mitte", "Kreuzberg"];

    [Fact]
    public async Task The_traversal_groups_the_related_rows_by_origin_record()
    {
        var body = await ExecuteAsync("f=json&objectIds=1&relationshipId=children&outFields=name&returnGeometry=false");

        var group = Assert.Single(body.GetProperty("relationships").EnumerateArray());
        Assert.Equal("children", group.GetProperty("name").GetString());
        Assert.Equal(1, group.GetProperty("relatedId").GetInt32());
        Assert.Equal(
            MitteAndKreuzberg,
            group.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("attributes").GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task The_traversal_serves_the_related_layers_field_metadata()
    {
        var body = await ExecuteAsync("f=json&objectIds=1&relationshipId=children&outFields=name&returnGeometry=false");

        var names = body.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()).ToArray();
        Assert.Contains("parent_id", names);
        Assert.Contains("OBJECTID", names);
    }

    [Fact]
    public async Task The_traversal_applies_the_related_layers_where_clause()
    {
        var body = await ExecuteAsync("f=json&objectIds=1&relationshipId=children&where=name%3D'Kreuzberg'&outFields=name&returnGeometry=false");

        var row = Assert.Single(Assert.Single(body.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray());
        Assert.Equal("Kreuzberg", row.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_traversal_never_claims_a_related_record_whose_key_is_null()
    {
        // The orphan child belongs to nobody: a null key is not "related to
        // everything", so it is left out rather than swept in.
        var body = await ExecuteAsync("f=json&objectIds=1&relationshipId=children&outFields=name&returnGeometry=false");

        var names = Assert.Single(body.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("attributes").GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("Orphan", names);
    }

    [Fact]
    public async Task The_traversal_rejects_an_unknown_origin_record()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => ExecuteAsync("f=json&objectIds=99&relationshipId=children"));

        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
    }

    [Fact]
    public async Task The_traversal_answers_a_cancelled_request()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            "f=json&objectIds=1&relationshipId=children", cancellation.Token));
    }

    private sealed class StubRegistry : IMapRegistry, IStoreRegistry
    {
        private static readonly Feature ParentsTable = new(
            new FeatureId("1"), ParentSchema,
            [AttributeValue.FromInt64(1), AttributeValue.FromString("Berlin"), Geometry()]);

        private static readonly Feature ChildrenTable = new(
            new FeatureId("10"), ChildSchema,
            [AttributeValue.FromInt64(10), AttributeValue.FromInt64(1), AttributeValue.FromString("Mitte"), Geometry()]);

        private static readonly Feature Kreuzberg = new(
            new FeatureId("11"), ChildSchema,
            [AttributeValue.FromInt64(11), AttributeValue.FromInt64(1), AttributeValue.FromString("Kreuzberg"), Geometry()]);

        private static readonly Feature Orphan = new(
            new FeatureId("12"), ChildSchema,
            [AttributeValue.FromInt64(12), AttributeValue.Null, AttributeValue.FromString("Orphan"), Geometry()]);

        private static readonly Map Published = new(
            "test",
            "test",
            [
                new MapLayer("test.parents", 0, "parents", Relationships: [Declared]),
                new MapLayer("test.children", 1, "children"),
            ],
            [MapServiceKind.FeatureServer]);

        public Task<Map> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Published);

        public Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Map>>([Published]);

        public Task<Map> PutAsync(Map map, CancellationToken cancellationToken = default) => Task.FromResult(map);

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public IDataCatalogue Catalogue(string store) => new StubCatalogue();

        public IFeatureStore Features(string store) => new StubTable();

        public IFeatureEditStore? EditStore(string store) => null;

        public IFeatureAttachmentStore? AttachmentStore(string store) => null;

        public ITransactionStore? Transactions(string store) => null;

        public IDatasetIngest? Ingest(string store) => null;

        public IRasterCatalogue? RasterCatalogue(string store) => null;

        private static AttributeValue Geometry() => AttributeValue.FromGeometry(new Point(new Coordinate(13.4, 52.5), CoordinateReference.Epsg(4326)));

        private sealed class StubCatalogue : IDataCatalogue
        {
            public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
                Task.FromResult(dataset == Parents.Id ? Parents : Children);

            public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<DatasetSummary>>([]);

            public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
                Task.FromResult(dataset);
        }

        /// <summary>
        /// A table that hands out its rows and ignores cancellation, so the
        /// traversal's own token checks are what the test observes.
        /// </summary>
        private sealed class StubTable : IFeatureStore
        {
            public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<FeatureBatch>>(
                    dataset == Parents.Id
                        ? [new FeatureBatch(Parents.Schema, [ParentsTable])]
                        : [new FeatureBatch(ChildSchema, [ChildrenTable, Kreuzberg, Orphan])]);

            public Task<FeatureQueryPage> QueryAsync(
                string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
                Spatial.Querying.FeaturePlanFallback.ReadAsync(this, dataset, query, cancellationToken);

            public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(batch.Count);
        }
    }
}
