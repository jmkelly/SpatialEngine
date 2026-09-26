using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The containerised edit-path tests (ADR-0037): add/update/delete through
/// <c>SqlServerEditStore</c> — success, per-feature failure and cancellation
/// for <c>DeleteAsync</c>, <c>EditBatchAsync</c> (via add/update) and
/// <c>DeleteFeatureAsync</c>. Every test skips with an explicit reason when no
/// Docker daemon is available.
/// </summary>
public sealed class SqlServerEditIntegrationTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerEditIntegrationTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Add_inserts_each_feature_and_reports_success()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_add_target");
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one"), EditFeature(schema, "2", "two")]);

        var outcomes = await context.Editor.AddAsync(dataset, batch);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded, outcome.ErrorMessage));
        Assert.Equal(["1", "2"], outcomes.Select(outcome => outcome.Id.Value).ToArray());
        Assert.Equal(2, await context.CountAsync($"SELECT count(*) FROM {dataset}"));
    }

    [SkippableFact]
    public async Task Add_without_an_identity_uses_the_database_assigned_value()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("IF OBJECT_ID('dbo.edit_identity_target', 'U') IS NOT NULL DROP TABLE dbo.edit_identity_target");
        await context.ExecuteAsync(
            "CREATE TABLE dbo.edit_identity_target (id int NOT NULL IDENTITY(1,1) PRIMARY KEY, name nvarchar(50) NULL, geom geometry NULL)");
        var schema = EditSchema();
        var feature = new Feature(FeatureId.Unassigned, schema,
        [
            AttributeValue.FromInt64(0),
            AttributeValue.FromString("auto"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);

        var outcome = Assert.Single(await context.Editor.AddAsync(
            "dbo.edit_identity_target", new FeatureBatch(schema, [feature])));

        Assert.True(outcome.Succeeded, outcome.ErrorMessage);
        Assert.NotEqual(FeatureId.Unassigned, outcome.Id);
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM dbo.edit_identity_target"));
    }

    [SkippableFact]
    public async Task Add_reports_per_feature_failure_on_duplicate_identity()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_dup_target");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var outcomes = await context.Editor.AddAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "2", "two"), EditFeature(schema, "1", "clash")]));

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcomes[1].ErrorCode);
        Assert.Equal("1", outcomes[1].Id.Value);
        Assert.Equal(2, await context.CountAsync($"SELECT count(*) FROM {dataset}"));
    }

    [SkippableFact]
    public async Task Update_replaces_the_matching_feature()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_update_target");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "1", "uno")])));

        Assert.True(outcome.Succeeded);
        Assert.Equal("1", outcome.Id.Value);
        var scanned = (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Single();
        Assert.Equal("uno", scanned["name"].StringValue);
    }

    [SkippableFact]
    public async Task Update_of_a_missing_feature_reports_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_update_miss_target");
        var schema = EditSchema();

        var outcome = Assert.Single(await context.Editor.UpdateAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "404", "ghost")])));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.NotFound, outcome.ErrorCode);
        Assert.Equal("404", outcome.Id.Value);
    }

    [SkippableFact]
    public async Task Delete_removes_the_feature_and_reports_not_found_for_a_miss()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_delete_target");
        var schema = EditSchema();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            dataset, new FeatureBatch(schema, [EditFeature(schema, "1", "one")]))).Succeeded);

        var deleted = Assert.Single(await context.Editor.DeleteAsync(dataset, [new FeatureId("1")]));
        Assert.True(deleted.Succeeded);
        Assert.Equal("1", deleted.Id.Value);
        Assert.Equal(0, await context.CountAsync($"SELECT count(*) FROM {dataset}"));

        var miss = Assert.Single(await context.Editor.DeleteAsync(dataset, [new FeatureId("1")]));
        Assert.False(miss.Succeeded);
        Assert.Equal(SpatialException.NotFound, miss.ErrorCode);
    }

    [SkippableFact]
    public async Task Delete_with_a_malformed_identity_reports_invalid_arguments()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        // dbo.places has one identity column; a two-part identity cannot be parsed.
        var outcome = Assert.Single(await context.Editor.DeleteAsync("dbo.places", [new FeatureId("1|2")]));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, outcome.ErrorCode);
        Assert.Equal(3, await context.CountAsync("SELECT count(*) FROM dbo.places"));
    }

    [SkippableFact]
    public async Task Edit_rejects_a_batch_whose_schema_does_not_match_the_dataset()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var schema = new FeatureSchema([new FieldDefinition("mystery", AttributeKind.String, false)]);
        var batch = new FeatureBatch(schema, []);

        var add = await Assert.ThrowsAsync<SpatialException>(() => context.Editor.AddAsync("dbo.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() => context.Editor.UpdateAsync("dbo.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);
    }

    [SkippableFact]
    public async Task Edit_on_a_table_without_a_primary_key_reports_invalid_arguments_per_feature()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID('dbo.unkeyed', 'U') IS NOT NULL DROP TABLE dbo.unkeyed; "
            + "CREATE TABLE dbo.unkeyed (id int NULL, kind nvarchar(20) NULL, geom geometry NULL)");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, true),
            new FieldDefinition("kind", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema,
            [
                AttributeValue.FromInt64(1),
                AttributeValue.FromString("highway"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(4326))),
            ]),
        ]);

        var added = Assert.Single(await context.Editor.AddAsync("dbo.unkeyed", batch));
        Assert.False(added.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, added.ErrorCode);

        var updated = Assert.Single(await context.Editor.UpdateAsync("dbo.unkeyed", batch));
        Assert.False(updated.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, updated.ErrorCode);

        var deleted = Assert.Single(await context.Editor.DeleteAsync("dbo.unkeyed", [new FeatureId("1")]));
        Assert.False(deleted.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, deleted.ErrorCode);
    }

    [SkippableFact]
    public async Task Edit_with_an_unknown_transaction_is_invalid_arguments()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one")]);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.AddAsync("dbo.places", batch, "missing"));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.UpdateAsync("dbo.places", batch, "missing"));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Editor.DeleteAsync("dbo.places", [new FeatureId("1")], "missing"));
        Assert.Equal(SpatialException.InvalidArguments, delete.Code);
    }

    [SkippableFact]
    public async Task Edit_inside_a_transaction_becomes_durable_on_commit()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await TargetAsync(context, "edit_tx_target");
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one")]);

        var transaction = await context.Store.BeginAsync();
        Assert.True(Assert.Single(await context.Editor.AddAsync(dataset, batch, transaction)).Succeeded);

        Assert.True(await context.Store.RollbackAsync(transaction));
        Assert.Equal(0, await context.CountAsync($"SELECT count(*) FROM {dataset}"));

        var second = await context.Store.BeginAsync();
        Assert.True(Assert.Single(await context.Editor.AddAsync(dataset, batch, second)).Succeeded);
        Assert.True(await context.Store.CommitAsync(second));
        Assert.Equal(1, await context.CountAsync($"SELECT count(*) FROM {dataset}"));
    }

    [SkippableFact]
    public async Task Edit_honours_cancellation()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var schema = EditSchema();
        var batch = new FeatureBatch(schema, [EditFeature(schema, "1", "one")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Editor.AddAsync("dbo.places", batch, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Editor.UpdateAsync("dbo.places", batch, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Editor.DeleteAsync("dbo.places", [new FeatureId("1")], cancellationToken: cancellation.Token));
    }

    /// <summary>
    /// A fresh empty dataset with the edit schema, keyed on <c>id</c> and
    /// created through the store so its SRID is recorded: the edits below must
    /// not depend on a table some earlier test left behind.
    /// </summary>
    private static async Task<string> TargetAsync(SqlServerTestContext context, string name)
    {
        var dataset = $"dbo.{name}";
        await context.ExecuteAsync($"IF OBJECT_ID('{dataset}', 'U') IS NOT NULL DROP TABLE {dataset}");
        var schema = EditSchema();
        await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326, IngestIdentity.Source, "id"),
            [new FeatureBatch(schema, [EditFeature(schema, "1", "seed")])]);
        await context.ExecuteAsync($"DELETE FROM {dataset} WHERE [id] = 1");
        return dataset;
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
