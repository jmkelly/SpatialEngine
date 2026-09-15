using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised data-path tests (ADR-0033): catalogue, schema
/// discovery, scan/query with canonical feature batches, bbox and
/// parameterised attribute filtering, writing, result-table creation,
/// transactions (commit/rollback), cancellation and secret redaction —
/// against a real PostGIS container. Every test skips with an explicit
/// reason when no Docker daemon is available.
/// </summary>
public sealed class PostgisIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Catalogue_lists_the_seeded_datasets()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var ids = (await context.Store.ListAsync()).Select(summary => summary.Id).ToArray();

        Assert.Contains("public.places", ids);
        Assert.Contains("public.roads", ids);
        Assert.Contains("public.bigpoints", ids);
    }

    [SkippableFact]
    public async Task Catalogue_pattern_filters_the_listed_datasets()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var ids = (await context.Store.ListAsync("%places%")).Select(summary => summary.Id).ToArray();

        Assert.Equal(["public.places"], ids);
    }

    [SkippableFact]
    public async Task Describe_reports_the_schema_geometry_srid_and_identity_columns()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var description = await context.Store.DescribeAsync("public.places");

        Assert.Equal("public.places", description.Id);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(4326, description.Srid);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(
            [$"id|{AttributeKind.Int64}", $"name|{AttributeKind.String}", $"geom|{AttributeKind.Geometry}"],
            description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}").ToArray());
    }

    [SkippableFact]
    public async Task Describe_of_an_unknown_dataset_is_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync("public.nowhere"));

        Assert.Equal(SpatialException.NotFound, exception.Code);
    }

    [SkippableFact]
    public async Task Scan_returns_canonical_batches_with_crs_stamped_geometry()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var batches = await context.Store.ScanAsync("public.places");

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(3, features.Length);

        var berlin = features.Single(feature => feature["name"].StringValue == "Berlin");
        Assert.Equal("1", berlin.Id.Value);
        var point = Assert.IsType<Point>(berlin[2].GeometryValue);
        Assert.Equal(13.405, (double)point.X!, precision: 12);
        Assert.Equal(52.52, (double)point.Y!, precision: 12);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), point.CoordinateReference);
    }

    [SkippableFact]
    public async Task Scan_of_a_table_without_a_primary_key_uses_ordinal_identities()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var batches = await context.Store.ScanAsync("public.roads");

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(2, features.Length);
        Assert.Equal(["0", "1"], features.Select(feature => feature.Id.Value).ToArray());
        Assert.Equal(GeometryType.LineString, features[0][2].GeometryValue.Type);
    }

    [SkippableFact]
    public async Task Query_bounding_box_returns_only_intersecting_features()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var batches = await context.Store.QueryAsync(
            "public.places", new BoundingBox(13.0, 52.0, 13.5, 53.0));

        var names = batches.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray();
        Assert.Equal(["Berlin"], names);
    }

    [SkippableFact]
    public async Task Query_attribute_filters_are_parameterised_and_resolved_against_the_schema()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var byName = await context.Store.QueryAsync("public.places", null, "name = 'Berlin'");
        Assert.Equal(["Berlin"], byName.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var like = await context.Store.QueryAsync("public.places", null, "name LIKE 'P%'");
        Assert.Equal(["Paris"], like.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var prefix = await context.Store.QueryAsync("public.places", null, "id < 3 AND name != 'London'");
        Assert.Equal(["Berlin"], prefix.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var filteredBox = await context.Store.QueryAsync(
            "public.places", new BoundingBox(-1.0, 48.0, 15.0, 54.0), "id = 3");
        Assert.Equal(["Paris"], filteredBox.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var unknownColumn = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.QueryAsync("public.places", null, "mystery = 1"));
        Assert.Equal(SpatialException.InvalidArguments, unknownColumn.Code);
        Assert.Contains("'mystery'", unknownColumn.Message);

        var geometryColumn = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.QueryAsync("public.places", null, "geom = 1"));
        Assert.Equal(SpatialException.InvalidArguments, geometryColumn.Code);
        Assert.Contains("bounding box", geometryColumn.Message);
    }

    [SkippableFact]
    public async Task Write_appends_features_in_a_single_transaction()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.write_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.write_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 3857))");

        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("10"), schema,
            [
                AttributeValue.FromInt64(10),
                AttributeValue.FromString("ten"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(10, 10, CoordinateReference.Epsg(3857))),
            ]),
            new Feature(new FeatureId("11"), schema,
            [
                AttributeValue.FromInt64(11),
                AttributeValue.FromString("eleven"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(11, 11, CoordinateReference.Epsg(3857))),
            ]),
        ]);

        Assert.Equal(2, await context.Store.WriteAsync("public.write_target", batch));
        Assert.Equal(2, await context.CountAsync("SELECT count(*) FROM public.write_target"));
    }

    [SkippableFact]
    public async Task Write_rejects_a_batch_whose_schema_does_not_match_the_dataset()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var schema = new FeatureSchema([new FieldDefinition("mystery", AttributeKind.String, false)]);
        var batch = new FeatureBatch(schema, []);

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("public.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [SkippableFact]
    public async Task Write_with_an_unknown_transaction_is_rejected()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("10"), schema,
            [
                AttributeValue.FromInt64(10),
                AttributeValue.FromString("ten"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(10, 10, CoordinateReference.Epsg(4326))),
            ]),
        ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("public.places", batch, "missing"));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("Unknown transaction", failure.Message);
    }

    [SkippableFact]
    public async Task Create_builds_a_result_table_from_a_defining_batch()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.created");

        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batch = new FeatureBatch(schema, []);

        Assert.Equal("public.created", await context.Store.CreateAsync("public.created", batch, 3857));

        var description = await context.Store.DescribeAsync("public.created");
        Assert.Equal(3857, description.Srid);
        Assert.Equal("geom", description.GeometryColumn);
    }

    [SkippableFact]
    public async Task Committed_enlisted_writes_become_visible_after_commit()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.tx_target");
        await context.ExecuteAsync("CREATE TABLE public.tx_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

        var transaction = await context.Store.BeginAsync();
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema,
            [
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]),
        ]);
        Assert.Equal(1, await context.Store.WriteAsync("public.tx_target", batch, transaction));
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.tx_target"));

        Assert.True(await context.Store.CommitAsync(transaction));
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.tx_target"));

        var again = await Assert.ThrowsAsync<SpatialException>(() => context.Store.CommitAsync(transaction));
        Assert.Equal(SpatialException.InvalidArguments, again.Code);
        Assert.Contains("Unknown transaction", again.Message);
    }

    [SkippableFact]
    public async Task Rolled_back_enlisted_writes_never_become_visible()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.tx_target");
        await context.ExecuteAsync("CREATE TABLE public.tx_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema,
            [
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]),
        ]);

        var transaction = await context.Store.BeginAsync();
        await context.Store.WriteAsync("public.tx_target", batch, transaction);

        Assert.True(await context.Store.RollbackAsync(transaction));
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.tx_target"));
    }

    [SkippableFact]
    public async Task A_write_failure_keeps_the_implicit_transaction_pure()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.atomic_target");
        await context.ExecuteAsync("CREATE TABLE public.atomic_target (id bigint PRIMARY KEY, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batch = new FeatureBatch(schema, [FeatureWithId("1", schema), FeatureWithId("1", schema)]);

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("public.atomic_target", batch));
        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.atomic_target"));
    }

    [SkippableFact]
    public async Task Cancelling_a_scan_throws_operation_cancelled()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Store.ScanAsync("public.bigpoints", cancellation.Token));
    }

    [SkippableFact]
    public async Task Diagnostics_never_leak_the_connection_secret()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var secret = $"TRUE-SECRET-{Guid.NewGuid():N}";
        var broken = new PostgisStore(new PostgisOptions
        {
            ConnectionString = $"Host=localhost;Port=5432;Database=spatial;Username=spatial;Password={secret}",
        });

        var exception = await Assert.ThrowsAsync<SpatialException>(() => broken.ListAsync());

        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        Assert.DoesNotContain(secret, exception.Message);
    }

    [SkippableFact]
    public async Task Edit_add_update_delete_round_trips_on_a_primary_key_table()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var feature = new Feature(new FeatureId("1"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("one"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);

        var added = await context.Editor.AddAsync("public.edit_target", new FeatureBatch(schema, [feature]));
        Assert.True(Assert.Single(added).Succeeded);
        Assert.Equal("1", added[0].Id.Value);
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.edit_target"));

        var renamed = new Feature(new FeatureId("1"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("uno"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(3, 4, CoordinateReference.Epsg(4326))),
        ]);
        Assert.True(Assert.Single(await context.Editor.UpdateAsync("public.edit_target", new FeatureBatch(schema, [renamed]))).Succeeded);
        var scanned = (await context.Store.ScanAsync("public.edit_target")).SelectMany(batch => batch.Features).Single();
        Assert.Equal("uno", scanned["name"].StringValue);

        Assert.True(Assert.Single(await context.Editor.DeleteAsync("public.edit_target", [new FeatureId("1")])).Succeeded);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.edit_target"));
        Assert.False(Assert.Single(await context.Editor.DeleteAsync("public.edit_target", [new FeatureId("1")])).Succeeded);
    }

    [SkippableFact]
    public async Task Edit_inside_a_transaction_is_not_visible_until_commit()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.edit_tx_target");
        await context.ExecuteAsync(
            "CREATE TABLE public.edit_tx_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var feature = new Feature(new FeatureId("1"), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromString("one"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);

        var transaction = await context.Store.BeginAsync();
        Assert.True(Assert.Single(await context.Editor.AddAsync("public.edit_tx_target", new FeatureBatch(schema, [feature]), transaction)).Succeeded);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM public.edit_tx_target"));

        Assert.True(await context.Store.CommitAsync(transaction));
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.edit_tx_target"));
    }

    [SkippableFact]
    public async Task Lookup_by_identity_returns_matching_features_and_ignores_misses()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var found = await context.Store.GetAsync(
            "public.places", [new FeatureId("1"), new FeatureId("3"), new FeatureId("404")]);

        Assert.Equal(["1", "3"], found.Select(feature => feature.Id.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        var berlin = found.Single(feature => feature.Id.Value == "1");
        Assert.Equal("Berlin", berlin["name"].StringValue);
        Assert.Equal(GeometryType.Point, berlin[2].GeometryValue.Type);
    }

    [SkippableFact]
    public async Task Lookup_by_identity_of_a_missing_feature_is_empty()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        Assert.Empty(await context.Store.GetAsync("public.places", [new FeatureId("404")]));
    }

    [SkippableFact]
    public async Task Lookup_by_identity_of_a_table_without_a_primary_key_is_empty()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        Assert.Empty(await context.Store.GetAsync("public.roads", [new FeatureId("1")]));
    }

    [SkippableFact]
    public async Task Lookup_by_a_malformed_identity_is_rejected()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.GetAsync("public.places", [new FeatureId("1|2")]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    private static Feature FeatureWithId(string id, FeatureSchema schema) =>
        new(new FeatureId(id), schema,
        [
            AttributeValue.FromInt64(1),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
        ]);
}
