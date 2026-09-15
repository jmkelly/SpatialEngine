using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The ephemeral in-memory provider (ADR-0042): ingest identity modes, the
/// read/write/edit/lookup faces, transactions and the non-durable diagnostics
/// — all without a database.
/// </summary>
public sealed class MemoryStoreTests
{
    private static readonly FeatureSchema Source = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static FeatureBatch Batch(params Feature[] features) => new(Source, features);

    private static Feature Point(string id, string name, double x, double y) =>
        new(
            new FeatureId(id),
            Source,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    private static async Task<MemoryStore> IngestedAsync(IngestIdentity identity = IngestIdentity.Auto, string? field = null)
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);
        await ingest.IngestAsync(
            new IngestRequest("memory.cities", 4326, identity, field),
            [Batch(Point("1", "Berlin", 13.4, 52.5), Point("2", "Paris", 2.35, 48.85))]);
        return store;
    }

    [Fact]
    public void The_store_is_explicitly_non_durable()
    {
        Assert.False(MemoryStore.Durable);
    }

    [Fact]
    public async Task Auto_ingest_assigns_an_identity_and_reports_it()
    {
        var store = await IngestedAsync();

        var description = await store.DescribeAsync("memory.cities");
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(2, description.EstimatedRowCount);

        var batches = await store.ScanAsync("memory.cities");
        var ids = batches.SelectMany(batch => batch.Features).Select(feature => feature.Id.Value).ToArray();
        Assert.Equal(["1", "2"], ids);
    }

    [Fact]
    public async Task Describe_reports_the_inferred_geometry_type()
    {
        var store = await IngestedAsync();

        Assert.Equal("Point", (await store.DescribeAsync("memory.cities")).GeometryType);
    }

    [Fact]
    public async Task Source_ingest_uses_the_named_field()
    {
        var store = new MemoryStore();
        var schema = new FeatureSchema(
        [
            new FieldDefinition("code", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(
            new FeatureId("7"),
            schema,
            [
                AttributeValue.FromInt64(7),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]);

        var outcome = await new MemoryIngest(store).IngestAsync(
            new IngestRequest("memory.things", 4326, IngestIdentity.Source, "code"),
            [new FeatureBatch(schema, [feature])]);

        Assert.Equal("code", outcome.IdentityField);
        Assert.Equal(["code"], (await store.DescribeAsync("memory.things")).IdColumns);
    }

    [Fact]
    public async Task None_ingest_creates_a_data_only_dataset()
    {
        var store = await IngestedAsync(IngestIdentity.None);

        Assert.Empty((await store.DescribeAsync("memory.cities")).IdColumns);
        var outcome = await new MemoryEditor(store).AddAsync("memory.cities", Batch(Point("9", "Rome", 12.5, 41.9)));
        Assert.False(outcome[0].Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcome[0].ErrorCode);
    }

    [Fact]
    public async Task A_duplicate_dataset_is_rejected()
    {
        var store = await IngestedAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => new MemoryIngest(store).IngestAsync(
            new IngestRequest("memory.cities", 4326), [Batch(Point("1", "Berlin", 13.4, 52.5))]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Add_with_an_unassigned_identity_assigns_the_next_id()
    {
        var store = await IngestedAsync();
        var editor = new MemoryEditor(store);

        var outcome = await editor.AddAsync("memory.cities", Batch(Point("__unassigned__", "Rome", 12.5, 41.9)));

        Assert.True(outcome[0].Succeeded);
        Assert.Equal("3", outcome[0].Id.Value);
    }

    [Fact]
    public async Task Adding_a_supplied_identity_keeps_it()
    {
        var store = await IngestedAsync();

        var outcome = await new MemoryEditor(store).AddAsync("memory.cities", Batch(Point("42", "Rome", 12.5, 41.9)));

        Assert.Equal("42", outcome[0].Id.Value);
        var lookup = await store.GetAsync("memory.cities", [new FeatureId("42")]);
        Assert.Single(lookup);
    }

    [Fact]
    public async Task Update_replaces_and_delete_removes()
    {
        var store = await IngestedAsync();
        var editor = new MemoryEditor(store);

        var updated = await editor.UpdateAsync("memory.cities", Batch(Point("1", "Munich", 11.5, 48.1)));
        Assert.True(updated[0].Succeeded);
        var found = await store.GetAsync("memory.cities", [new FeatureId("1")]);
        Assert.Equal("Munich", found[0]["name"].StringValue);

        var deleted = await editor.DeleteAsync("memory.cities", [new FeatureId("2")]);
        Assert.True(deleted[0].Succeeded);
        Assert.Empty(await store.GetAsync("memory.cities", [new FeatureId("2")]));
    }

    [Fact]
    public async Task A_bbox_query_returns_intersecting_features()
    {
        var store = await IngestedAsync();

        var batches = await store.QueryAsync("memory.cities", new BoundingBox(-1, 47, 4, 50));

        Assert.Equal(["2"], batches.SelectMany(batch => batch.Features).Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public async Task An_attribute_filter_is_rejected()
    {
        var store = await IngestedAsync();

        await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync("memory.cities", filter: "name = 'Berlin'"));
    }

    [Fact]
    public async Task A_rollback_restores_the_prior_state()
    {
        var store = await IngestedAsync();
        var editor = new MemoryEditor(store);

        var handle = await store.BeginAsync();
        await editor.AddAsync("memory.cities", Batch(Point("3", "Rome", 12.5, 41.9)));
        Assert.True(await store.RollbackAsync(handle));
        Assert.Empty(await store.GetAsync("memory.cities", [new FeatureId("3")]));

        var second = await store.BeginAsync();
        await editor.AddAsync("memory.cities", Batch(Point("3", "Rome", 12.5, 41.9)));
        Assert.True(await store.CommitAsync(second));
        Assert.Single(await store.GetAsync("memory.cities", [new FeatureId("3")]));
    }

    [Fact]
    public async Task An_edit_with_an_unknown_transaction_is_rejected()
    {
        var store = await IngestedAsync();

        await Assert.ThrowsAsync<SpatialException>(() =>
            new MemoryEditor(store).AddAsync("memory.cities", Batch(Point("3", "Rome", 12.5, 41.9)), "nope"));
    }

    [Fact]
    public async Task An_unknown_dataset_is_a_not_found()
    {
        var store = new MemoryStore();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.DescribeAsync("memory.nope"));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task A_batch_with_an_unknown_field_is_rejected()
    {
        var store = await IngestedAsync();
        var wrong = new FeatureSchema(
        [
            new FieldDefinition("nope", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(
            new FeatureId("9"),
            wrong,
            [
                AttributeValue.FromString("x"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            new MemoryEditor(store).AddAsync("memory.cities", new FeatureBatch(wrong, [feature])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Create_builds_an_empty_dataset_from_a_sample()
    {
        var store = new MemoryStore();

        var id = await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        Assert.Equal("memory.places", id);
        Assert.Equal(0, (await store.DescribeAsync("memory.places")).EstimatedRowCount);
    }

    [Fact]
    public async Task Write_round_trips_through_create_and_scan()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        var appended = await store.WriteAsync(
            "memory.places",
            Batch(Point("1", "Berlin", 13.4, 52.5), Point("2", "Paris", 2.35, 48.85)));

        Assert.Equal(2, appended);
        var batches = await store.ScanAsync("memory.places");
        Assert.Equal(["1", "2"], batches.SelectMany(batch => batch.Features).Select(feature => feature.Id.Value).ToArray());
        Assert.Single(await store.GetAsync("memory.places", [new FeatureId("1")]));
    }

    [Fact]
    public async Task Write_rejects_a_null_batch()
    {
        var store = new MemoryStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteAsync("memory.places", null!));
    }

    [Fact]
    public async Task Write_to_an_unknown_dataset_is_a_not_found()
    {
        var store = new MemoryStore();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.WriteAsync("memory.nope", Batch(Point("1", "Berlin", 13.4, 52.5))));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task Write_with_an_unknown_transaction_is_rejected()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.WriteAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), "nope"));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Empty((await store.ScanAsync("memory.places")).SelectMany(batch => batch.Features));
    }

    [Fact]
    public async Task Write_inside_a_transaction_rolls_back_with_it()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        var handle = await store.BeginAsync();
        await store.WriteAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), handle);
        Assert.Single((await store.ScanAsync("memory.places")).SelectMany(batch => batch.Features));
        Assert.True(await store.RollbackAsync(handle));

        Assert.Empty((await store.ScanAsync("memory.places")).SelectMany(batch => batch.Features));
    }

    [Fact]
    public async Task Write_observes_cancellation()
    {
        var store = new MemoryStore();
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.WriteAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), cancellationToken: canceled));
    }
}
