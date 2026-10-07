using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
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
    public async Task Every_ingest_creates_a_keyed_dataset()
    {
        // ADR-0149: there is no keyless ingest any more, so the default
        // request is the keyed one — it declares an identity column and is
        // therefore editable (ADR-0037) as well as lookup-able.
        var store = await IngestedAsync();

        Assert.Equal(["id"], (await store.DescribeAsync("memory.cities")).IdColumns);
        var outcome = await new MemoryEditor(store).AddAsync("memory.cities", Batch(Point("9", "Rome", 12.5, 41.9)));
        Assert.True(outcome[0].Succeeded);
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

    // A store keys its features by the identity column (ADR-0038), so an add
    // whose identity already exists is a primary-key collision in PostGIS and
    // SQL Server, reported per feature. Memory appended to a list and accepted
    // it, storing two features under one id and making a read-by-identity
    // return both.
    [Fact]
    public async Task Adding_an_identity_that_already_exists_fails_per_feature()
    {
        var store = await IngestedAsync();

        var failure = Assert.Single(await new MemoryEditor(store).AddAsync(
            "memory.cities", Batch(Point("1", "Berlin again", 13.4, 52.5))));

        Assert.False(failure.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, failure.ErrorCode);
        var found = await store.GetAsync("memory.cities", [new FeatureId("1")]);
        Assert.Equal("Berlin", Assert.Single(found)["name"].StringValue);
    }

    /// <summary>
    /// The reported reproduction: a source-identity dataset keys each feature
    /// by the identity column's value (ADR-0038, ADR-0112), so an add of an
    /// identity the dataset already holds is the collision the keyed stores
    /// report, not a second row under the same key.
    /// </summary>
    [Fact]
    public async Task A_source_identity_dataset_refuses_a_duplicate_explicit_identity()
    {
        var store = new MemoryStore();
        var schema = new FeatureSchema(
        [
            new FieldDefinition("code", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        Feature Row(string id, long code) => new(
            new FeatureId(id),
            schema,
            [
                AttributeValue.FromInt64(code),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
            ]);

        await new MemoryIngest(store).IngestAsync(
            new IngestRequest("memory.children", 4326, IngestIdentity.Source, "code"),
            [new FeatureBatch(schema, [Row("10", 10)])]);

        var failure = Assert.Single(await new MemoryEditor(store).AddAsync(
            "memory.children", new FeatureBatch(schema, [Row("10", 10)])));

        Assert.False(failure.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, failure.ErrorCode);
        Assert.Single(await store.GetAsync("memory.children", [new FeatureId("10")]));
        Assert.Single((await store.ScanAsync("memory.children")).SelectMany(page => page.Features));
    }

    [Fact]
    public async Task A_batch_that_repeats_an_identity_fails_only_the_later_feature()
    {
        var store = await IngestedAsync();

        var outcomes = await new MemoryEditor(store).AddAsync(
            "memory.cities", Batch(Point("9", "Rome", 12.5, 41.9), Point("9", "Rome twice", 12.5, 41.9)));

        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcomes[1].ErrorCode);
        Assert.Single(await store.GetAsync("memory.cities", [new FeatureId("9")]));
    }

    /// <summary>
    /// A store-assigned identity is the store's to choose (ADR-0043), so it
    /// takes the next free one: a value an explicit add already holds is
    /// skipped rather than stored twice under one key (ADR-0038).
    /// </summary>
    [Fact]
    public async Task An_assigned_identity_skips_one_an_explicit_add_already_took()
    {
        var store = await IngestedAsync();
        var editor = new MemoryEditor(store);

        var taken = await editor.AddAsync("memory.cities", Batch(Point("3", "Rome", 12.5, 41.9)));
        Assert.True(taken[0].Succeeded);

        var assigned = await editor.AddAsync("memory.cities", Batch(Point("__unassigned__", "Naples", 14.3, 40.85)));

        Assert.True(assigned[0].Succeeded);
        Assert.Equal("4", assigned[0].Id.Value);
        Assert.Single(await store.GetAsync("memory.cities", [new FeatureId("4")]));
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

        var batches = (await store.QueryAsync("memory.cities", new FeatureQuery(BoundingBox: new BoundingBox(-1, 47, 4, 50)))).Batches;

        Assert.Equal(["2"], batches.SelectMany(batch => batch.Features).Select(f => f.Id.Value).ToArray());
    }

    // ADR-0074: MemoryStore is the reference evaluator for the one predicate
    // vocabulary every store shares, so the attribute filter the other
    // providers push down is answered here too (reproduction: the store used
    // to refuse the filter outright).
    [Fact]
    public async Task An_attribute_filter_selects_matching_features()
    {
        var store = await IngestedAsync();

        var batches = (await store.QueryAsync(
            "memory.cities", new FeatureQuery(Where: FeatureFilter.Parse("name = 'Berlin'")))).Batches;

        Assert.Equal(["1"], batches.SelectMany(batch => batch.Features).Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public async Task A_bbox_and_a_filter_narrow_together()
    {
        var store = await IngestedAsync();

        var batches = (await store.QueryAsync("memory.cities", new FeatureQuery(
            BoundingBox: new BoundingBox(-1, 47, 4, 50),
            Where: FeatureFilter.Parse("name != 'Nowhere'")))).Batches;

        Assert.Equal(["2"], batches.SelectMany(batch => batch.Features).Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public async Task An_identity_restriction_selects_the_named_features()
    {
        var store = await IngestedAsync();

        var batches = (await store.QueryAsync(
            "memory.cities", new FeatureQuery(Ids: [new FeatureId("2")]))).Batches;

        Assert.Equal(["2"], batches.SelectMany(batch => batch.Features).Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public async Task A_filter_on_an_unknown_field_is_invalid_arguments()
    {
        var store = await IngestedAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync(
            "memory.cities", new FeatureQuery(Where: FeatureFilter.Parse("mystery = 1"))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'mystery'", failure.Message);
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
    }

    // A store keys its features by the identity column (ADR-0038, ADR-0112),
    // so a bulk write of an identity the dataset already holds is the
    // primary-key collision the keyed stores raise — not a second row under
    // one key, which a read-by-identity would then return twice.
    [Fact]
    public async Task Write_rejects_an_identity_the_dataset_already_holds()
    {
        var store = await IngestedAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.WriteAsync("memory.cities", Batch(Point("1", "Berlin again", 13.4, 52.5))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Single(await store.GetAsync("memory.cities", [new FeatureId("1")]));
        Assert.Equal(2, (await store.ScanAsync("memory.cities")).SelectMany(page => page.Features).Count());
    }

    [Fact]
    public async Task Write_rejects_a_batch_that_repeats_an_identity()
    {
        var store = await IngestedAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.WriteAsync(
            "memory.cities", Batch(Point("9", "Rome", 12.5, 41.9), Point("9", "Rome twice", 12.5, 41.9))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(2, (await store.ScanAsync("memory.cities")).SelectMany(page => page.Features).Count());
    }

    // The keyed stores write a colliding batch in one transaction and roll it
    // back, so the memory reference must refuse the batch before it appends
    // any of it rather than leaving the earlier features stored.
    [Fact]
    public async Task Write_rejects_the_whole_batch_when_one_identity_collides()
    {
        var store = await IngestedAsync();

        await Assert.ThrowsAsync<SpatialException>(() => store.WriteAsync(
            "memory.cities", Batch(Point("9", "Rome", 12.5, 41.9), Point("1", "Berlin again", 13.4, 52.5))));

        var ids = (await store.ScanAsync("memory.cities"))
            .SelectMany(page => page.Features).Select(feature => feature.Id.Value).ToArray();
        Assert.Equal(["1", "2"], ids);
    }

    // A create-built dataset declares no identity column (ADR-0140,
    // ADR-0147), so its Feature.Id is not a key and a primary-key collision is
    // not the question: the keyless table PostGIS creates accepts both rows,
    // and the reference store must not make a key of a value the contract says
    // is not one.
    [Fact]
    public async Task Write_into_a_keyless_dataset_does_not_police_the_feature_identity()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        var appended = await store.WriteAsync("memory.places", Batch(Point("1", "Berlin again", 13.4, 52.5)));

        Assert.Equal(1, appended);
        Assert.Single((await store.ScanAsync("memory.places")).SelectMany(page => page.Features));
    }

    // ADR-0140: a create-built dataset declares no identity column, so it has
    // no durable key to read a feature by. The lookup face says so rather than
    // answering from whatever identity the rows happen to carry — the refusal
    // is pinned by A_lookup_on_a_dataset_with_no_identity_column_is_refused.
    [Fact]
    public async Task A_create_built_dataset_declares_no_identity_column()
    {
        var store = new MemoryStore();

        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        Assert.Empty((await store.DescribeAsync("memory.places")).IdColumns);
    }

    [Fact]
    public async Task A_lookup_on_a_created_dataset_with_no_identity_column_is_refused()
    {
        // ADR-0149 ended the keyless *ingest*, and ADR-0147 keeps a
        // `CreateAsync`-built dataset keyless in the contract's view, so this
        // is the shape the ADR-0140 refusal now answers on.
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);
        await store.WriteAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)));

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => store.GetAsync("memory.places", [new FeatureId("1")]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("identity", failure.Message, StringComparison.Ordinal);
        Assert.Contains("memory.places", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_lookup_still_refuses_an_empty_id_list()
    {
        // The refusal is about the dataset, not about the identities asked
        // for: an empty request on an unkeyed dataset is still unanswerable.
        var store = new MemoryStore();
        await store.CreateAsync("memory.places", Batch(Point("1", "Berlin", 13.4, 52.5)), 4326);

        await Assert.ThrowsAsync<SpatialException>(() => store.GetAsync("memory.places", []));
    }

    [Fact]
    public async Task A_lookup_on_an_identity_backed_dataset_is_answered()
    {
        var store = await IngestedAsync();

        Assert.Equal("Berlin", Assert.Single(await store.GetAsync("memory.cities", [new FeatureId("1")]))["name"].StringValue);
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
