using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised edit-path tests (ADR-0037): add/update/delete through
/// <c>PostgisEditStore</c> — success, per-feature failure and cancellation
/// for <c>DeleteAsync</c>, <c>EditBatchAsync</c> (via add/update),
/// <c>ApplyFeatureAsync</c> and <c>DeleteFeatureAsync</c>. Every test skips
/// with an explicit reason when no Docker daemon is available.
/// </summary>
public sealed class PostgisEditIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisEditIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Add_inserts_each_feature_and_reports_success()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_add_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_add_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one"), EditFeature(schema, "2", "two")]);

        var outcomes = await context.Editor.AddAsync("public.edit_add_target", batch);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(["1", "2"], outcomes.Select(outcome => outcome.Id.Value).ToArray());
        Assert.Equal(2, await context.CountAsync("SELECT count(*) FROM public.edit_add_target"));
    }

    [SkippableFact]
    public async Task Add_without_an_identity_uses_the_database_assigned_value()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_identity_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_identity_target (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        var feature = new Feature(FeatureId.Unassigned, schema,
        [
            AttributeValue.FromInt64(0),
            AttributeValue.FromString("auto"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);

        var outcome = Assert.Single(await context.Editor.AddAsync(
            "public.edit_identity_target", new FeatureBatch(schema, [feature])));

        Assert.True(outcome.Succeeded);
        Assert.NotEqual(FeatureId.Unassigned, outcome.Id);
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.edit_identity_target"));
    }

    [SkippableFact]
    public async Task Add_reports_per_feature_failure_on_duplicate_identity()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_dup_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_dup_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            "public.edit_dup_target", new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var outcomes = await context.Editor.AddAsync(
            "public.edit_dup_target",
            new FeatureBatch(schema, [EditFeature(schema, "2", "two"), EditFeature(schema, "1", "clash")]));

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcomes[1].ErrorCode);
        Assert.Equal("1", outcomes[1].Id.Value);
        Assert.Equal(2, await context.CountAsync("SELECT count(*) FROM public.edit_dup_target"));
    }

    [SkippableFact]
    public async Task Update_replaces_the_matching_feature()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_update_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_update_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            "public.edit_update_target", new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            "public.edit_update_target", new FeatureBatch(schema, [EditFeature(schema, "1", "uno")])));

        Assert.True(outcome.Succeeded);
        Assert.Equal("1", outcome.Id.Value);
        var scanned = (await context.Store.ScanAsync("public.edit_update_target"))
            .SelectMany(batch => batch.Features).Single();
        Assert.Equal("uno", scanned["name"].StringValue);
    }

    [SkippableFact]
    public async Task Update_of_a_missing_feature_reports_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_update_miss_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_update_miss_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();

        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            "public.edit_update_miss_target", new FeatureBatch(schema, [EditFeature(schema, "404", "ghost")])));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.NotFound, outcome.ErrorCode);
        Assert.Equal("404", outcome.Id.Value);
    }

    [SkippableFact]
    public async Task Update_conflicting_with_an_existing_identity_reports_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_update_clash_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_update_clash_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        var seeded = await context.Editor.AddAsync(
            "public.edit_update_clash_target",
            new FeatureBatch(schema, [EditFeature(schema, "1", "one"), EditFeature(schema, "2", "two")]));
        Assert.All(seeded, outcome => Assert.True(outcome.Succeeded));

        // Re-key feature 2 onto the existing identity 1: the row is matched by
        // the pre-edit identity (2), so the UPDATE violates the primary key
        // and the per-feature outcome carries invalid.arguments — and row 1
        // is left untouched (no retarget onto the new attribute value).
        var clash = new Feature(new FeatureId("2"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("two"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);
        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            "public.edit_update_clash_target", new FeatureBatch(schema, [clash])));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcome.ErrorCode);
        var rows = (await context.Store.ScanAsync("public.edit_update_clash_target"))
            .SelectMany(batch => batch.Features)
            .ToDictionary(feature => feature.Id.Value, feature => feature["name"].StringValue);
        Assert.Equal("one", rows["1"]);
        Assert.Equal("two", rows["2"]);
    }

    [SkippableFact]
    public async Task Update_with_a_malformed_identity_reports_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_update_badid_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_update_badid_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();

        // public.edit_update_badid_target has one identity column; a two-part
        // identity cannot be parsed, so the per-feature outcome carries
        // invalid.arguments instead of throwing.
        var bad = new Feature(new FeatureId("1|2"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("one"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);
        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            "public.edit_update_badid_target", new FeatureBatch(schema, [bad])));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcome.ErrorCode);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.edit_update_badid_target"));
    }

    [SkippableFact]
    public async Task Edit_rejects_a_batch_whose_schema_does_not_match_the_dataset()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var schema = new FeatureSchema([new FieldDefinition("mystery", AttributeKind.String, false)]);
        var batch = new FeatureBatch(schema, []);

        var add = await Assert.ThrowsAsync<SpatialException>(() => context.Editor.AddAsync("public.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() => context.Editor.UpdateAsync("public.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);
    }

    [SkippableFact]
    public async Task Edit_on_a_table_without_a_primary_key_reports_invalid_arguments_per_feature()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        // public.roads is seeded without a primary key (see the fixture).
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, true),
            new FieldDefinition("kind", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var feature = new Feature(new FeatureId("1"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("highway"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(3857))),
        ]);
        var batch = new FeatureBatch(schema, [feature]);

        var added = Assert.Single(await context.Editor.AddAsync("public.roads", batch));
        Assert.False(added.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, added.ErrorCode);

        var updated = Assert.Single(await context.Editor.UpdateAsync("public.roads", batch));
        Assert.False(updated.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, updated.ErrorCode);

        var deleted = Assert.Single(await context.Editor.DeleteAsync("public.roads", [new FeatureId("1")]));
        Assert.False(deleted.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, deleted.ErrorCode);
    }

    [SkippableFact]
    public async Task Delete_removes_the_feature_and_reports_not_found_for_a_miss()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_delete_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_delete_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            "public.edit_delete_target", new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var deleted = Assert.Single(await context.Editor.DeleteAsync(
            "public.edit_delete_target", [new FeatureId("1")]));
        Assert.True(deleted.Succeeded);
        Assert.Equal("1", deleted.Id.Value);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.edit_delete_target"));

        var miss = Assert.Single(await context.Editor.DeleteAsync(
            "public.edit_delete_target", [new FeatureId("1")]));
        Assert.False(miss.Succeeded);
        Assert.Equal(SpatialException.NotFound, miss.ErrorCode);
    }

    [SkippableFact]
    public async Task Delete_with_a_malformed_identity_reports_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        // public.places has one identity column; a two-part identity cannot be parsed.
        var outcome = Assert.Single(await context.Editor.DeleteAsync(
            "public.places", [new FeatureId("1|2")]));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcome.ErrorCode);
        Assert.Equal(3, await context.CountAsync("SELECT count(*) FROM public.places"));
    }

    [SkippableFact]
    public async Task Edit_with_an_unknown_transaction_is_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one")]);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.AddAsync("public.places", batch, "missing"));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.UpdateAsync("public.places", batch, "missing"));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.DeleteAsync("public.places", [new FeatureId("1")], "missing"));
        Assert.Equal(SpatialException.InvalidArguments, delete.Code);
    }

    [SkippableFact]
    public async Task Edit_honours_cancellation()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one")]);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Editor.AddAsync("public.places", batch, cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Editor.UpdateAsync("public.places", batch, cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Editor.DeleteAsync("public.places", [new FeatureId("1")], cancellationToken: canceled));
    }

    private static FeatureSchema EditSchema() =>
        new(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

    private static Feature EditFeature(FeatureSchema schema, string id, string name) =>
        new(new FeatureId(id), schema,
        [
            AttributeValue.FromInt64(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)),
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);
}
