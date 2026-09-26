using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The containerised data-path tests (ADR-0072 as the SQL Server provider
/// follows ADR-0033): catalogue, schema discovery, scan/query with canonical
/// feature batches, bbox and parameterised attribute filtering, writing,
/// result-table creation, transactions (commit/rollback), cancellation and
/// secret redaction — against a real SQL Server container. Every test skips
/// with an explicit reason when no Docker daemon is available.
/// </summary>
public sealed class SqlServerIntegrationTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Catalogue_lists_the_seeded_datasets()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var ids = (await context.Store.ListAsync()).Select(summary => summary.Id).ToArray();

        Assert.Contains("dbo.places", ids);
        Assert.Contains("dbo.roads", ids);
        Assert.Contains("dbo.bigpoints", ids);
    }

    [SkippableFact]
    public async Task Catalogue_pattern_filters_the_listed_datasets()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var ids = (await context.Store.ListAsync("%places%")).Select(summary => summary.Id).ToArray();

        Assert.Equal(["dbo.places"], ids);
    }

    [SkippableFact]
    public async Task Catalogue_reports_the_sampled_srid_and_geometry_type()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var places = (await context.Store.ListAsync()).Single(summary => summary.Id == "dbo.places");

        Assert.Equal(4326, places.Srid);
        Assert.Equal("geom", places.GeometryColumn);
        Assert.True(places.EstimatedRowCount >= 3);
    }

    [SkippableFact]
    public async Task Describe_reports_the_schema_geometry_srid_and_identity_columns()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var description = await context.Store.DescribeAsync("dbo.places");

        Assert.Equal("dbo.places", description.Id);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(4326, description.Srid);
        Assert.Equal("Point", description.GeometryType);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(
            [$"id|{AttributeKind.Int64}", $"name|{AttributeKind.String}", $"geom|{AttributeKind.Geometry}"],
            description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}").ToArray());
    }

    [SkippableFact]
    public async Task Describe_of_an_unknown_dataset_is_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync("dbo.nowhere"));

        Assert.Equal(SpatialException.NotFound, exception.Code);
    }

    [SkippableFact]
    public async Task Describe_of_a_table_with_an_unsupported_column_is_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID('dbo.unsupported', 'U') IS NOT NULL DROP TABLE dbo.unsupported; "
            + "CREATE TABLE dbo.unsupported (id int NOT NULL, label xml NULL, geom geometry NULL);");

        var exception = await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync("dbo.unsupported"));

        Assert.Equal(SpatialException.NotFound, exception.Code);
        Assert.Contains("'xml'", exception.Message);
        Assert.Contains("label", exception.Message);
    }

    [SkippableFact]
    public async Task Scan_returns_canonical_batches_with_crs_stamped_geometry()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var batches = await context.Store.ScanAsync("dbo.places");

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
    public async Task Scan_reads_every_supported_column_type()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID('dbo.typed', 'U') IS NOT NULL DROP TABLE dbo.typed; "
            + "CREATE TABLE dbo.typed (id int NOT NULL PRIMARY KEY, amount float NULL, ratio decimal(9,3) NULL, "
            + "flag bit NULL, seen datetime2 NULL, stamp datetimeoffset NULL, tag uniqueidentifier NULL, geom geometry NULL); "
            + "INSERT INTO dbo.typed VALUES (1, 1.5, 0.125, 1, '2024-05-06T07:08:09', "
            + "'2024-05-06T07:08:09+02:00', '0f8fad5b-d9cb-469f-a165-70867728950e', NULL);");

        var feature = (await context.Store.ScanAsync("dbo.typed")).SelectMany(batch => batch.Features).Single();

        Assert.Equal(1.5, feature["amount"].DoubleValue, precision: 10);
        Assert.Equal(0.125, feature["ratio"].DoubleValue, precision: 10);
        Assert.True(feature["flag"].BooleanValue);
        Assert.Equal(
            new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero),
            feature["seen"].DateTimeOffsetValue.ToUniversalTime());
        Assert.Equal(
            new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)),
            feature["stamp"].DateTimeOffsetValue);
        Assert.Equal(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), feature["tag"].GuidValue);
        Assert.True(feature["geom"].IsNull);
    }

    [SkippableFact]
    public async Task Scan_of_a_table_without_a_primary_key_uses_ordinal_identities()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID('dbo.unkeyed', 'U') IS NOT NULL DROP TABLE dbo.unkeyed; "
            + "CREATE TABLE dbo.unkeyed (kind nvarchar(20) NULL, geom geometry NOT NULL); "
            + "INSERT INTO dbo.unkeyed VALUES (N'road', geometry::STGeomFromText('LINESTRING(0 0, 10 10)', 3857)), "
            + "(N'trail', geometry::STGeomFromText('LINESTRING(5 5, 20 20)', 3857));");

        var features = (await context.Store.ScanAsync("dbo.unkeyed")).SelectMany(batch => batch.Features).ToArray();

        Assert.Equal(2, features.Length);
        Assert.Equal(["0", "1"], features.Select(feature => feature.Id.Value).ToArray());
        Assert.Equal(GeometryType.LineString, features[0][1].GeometryValue.Type);
    }

    [SkippableFact]
    public async Task Query_bounding_box_returns_only_intersecting_features()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var batches = await context.Store.QueryAsync(
            "dbo.places", new BoundingBox(13.0, 52.0, 13.5, 53.0));

        var names = batches.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray();
        Assert.Equal(["Berlin"], names);
    }

    [SkippableFact]
    public async Task Query_a_degenerate_bounding_box_still_returns_the_covering_features()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        // A box with no width or no height degenerates to a line or a point;
        // the envelope must stay a valid geometry rather than a collapsed ring.
        var vertical = await context.Store.QueryAsync("dbo.places", new BoundingBox(13.405, 50.0, 13.405, 55.0));
        Assert.Equal(
            ["Berlin"],
            vertical.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var point = await context.Store.QueryAsync("dbo.places", new BoundingBox(2.3522, 48.8566, 2.3522, 48.8566));
        Assert.Equal(
            ["Paris"],
            point.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());
    }

    [SkippableFact]
    public async Task Query_of_an_inverted_bounding_box_is_invalid_arguments()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.QueryAsync("dbo.places", new BoundingBox(10.0, 10.0, 1.0, 1.0)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Query_attribute_filters_are_parameterised_and_resolved_against_the_schema()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var byName = await context.Store.QueryAsync("dbo.places", null, "name = 'Berlin'");
        Assert.Equal(["Berlin"], byName.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var like = await context.Store.QueryAsync("dbo.places", null, "name LIKE 'P%'");
        Assert.Equal(["Paris"], like.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var prefix = await context.Store.QueryAsync("dbo.places", null, "id < 3 AND name <> 'London'");
        Assert.Equal(["Berlin"], prefix.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var isNull = await context.Store.QueryAsync("dbo.places", null, "name IS NOT NULL");
        Assert.Equal(3, isNull.Sum(batch => batch.Count));

        var filteredBox = await context.Store.QueryAsync(
            "dbo.places", new BoundingBox(-1.0, 48.0, 15.0, 54.0), "id = 3");
        Assert.Equal(["Paris"], filteredBox.SelectMany(batch => batch.Features).Select(feature => feature["name"].StringValue).ToArray());

        var unknownColumn = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.QueryAsync("dbo.places", null, "mystery = 1"));
        Assert.Equal(SpatialException.InvalidArguments, unknownColumn.Code);
        Assert.Contains("'mystery'", unknownColumn.Message);

        var geometryColumn = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.QueryAsync("dbo.places", null, "geom = 1"));
        Assert.Equal(SpatialException.InvalidArguments, geometryColumn.Code);
        Assert.Contains("bounding box", geometryColumn.Message);
    }

    [SkippableFact]
    public async Task Write_appends_features_in_a_single_transaction()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await DropAsync(context, "dbo.write_target");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);

        // Created through the store, so the dataset's SRID is recorded and the
        // projected geometry below is not refused as a CRS conflict.
        await context.Store.CreateAsync("dbo.write_target", new FeatureBatch(schema, []), 3857);
        var batch = new FeatureBatch(schema,
        [
            PointFeature("10", "ten", 10, 10, 3857, schema),
            PointFeature("11", "eleven", 11, 11, 3857, schema),
        ]);

        Assert.Equal(2, await context.Store.WriteAsync("dbo.write_target", batch));
        Assert.Equal(2, await context.CountAsync("SELECT count(*) FROM dbo.write_target"));
    }

    [SkippableFact]
    public async Task Write_rejects_a_batch_whose_schema_does_not_match_the_dataset()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var schema = new FeatureSchema([new FieldDefinition("mystery", AttributeKind.String, false)]);
        var batch = new FeatureBatch(schema, []);

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("dbo.places", batch));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [SkippableFact]
    public async Task Write_rejects_a_geometry_whose_crs_conflicts_with_the_dataset()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        // dbo.places holds EPSG:4326 data, so a 3857 geometry conflicts with
        // the discovered dataset SRID.
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("dbo.places", new FeatureBatch(schema, [PointFeature("10", "ten", 1, 2, 3857, schema)])));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [SkippableFact]
    public async Task Write_with_an_unknown_transaction_is_rejected()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("dbo.places", new FeatureBatch(schema, [PointFeature("10", "ten", 10, 10, 4326, schema)]), "missing"));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("Unknown transaction", failure.Message);
    }

    [SkippableFact]
    public async Task Create_builds_a_result_table_from_a_defining_batch()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await DropAsync(context, "dbo.created");

        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

        Assert.Equal("dbo.created", await context.Store.CreateAsync("dbo.created", new FeatureBatch(schema, []), 3857));

        // The SRID of an empty dataset is read back from the provider's
        // metadata, not guessed, so a created dataset describes truthfully.
        var description = await context.Store.DescribeAsync("dbo.created");
        Assert.Equal(3857, description.Srid);
        Assert.Equal("geom", description.GeometryColumn);
    }

    [SkippableFact]
    public async Task Committed_enlisted_writes_become_visible_after_commit()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await DropAsync(context, "dbo.tx_target");
        await context.ExecuteAsync("CREATE TABLE dbo.tx_target (id int NOT NULL PRIMARY KEY, geom geometry NULL)");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

        var transaction = await context.Store.BeginAsync();
        var batch = new FeatureBatch(schema, [PointFeature("1", null, 1, 2, 4326, schema)]);
        Assert.Equal(1, await context.Store.WriteAsync("dbo.tx_target", batch, transaction));

        // No visibility check between the write and the commit: unlike
        // PostgreSQL's MVCC, SQL Server's default READ COMMITTED *blocks* on
        // another connection's uncommitted insert rather than hiding it, so
        // "not visible yet" is only observable as a lock wait (covered by
        // Rolling_back_... below, which asserts nothing survives).

        Assert.True(await context.Store.CommitAsync(transaction));
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM dbo.tx_target"));

        var again = await Assert.ThrowsAsync<SpatialException>(() => context.Store.CommitAsync(transaction));
        Assert.Equal(SpatialException.InvalidArguments, again.Code);
        Assert.Contains("Unknown transaction", again.Message);
    }

    [SkippableFact]
    public async Task Rolled_back_enlisted_writes_never_become_visible()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await DropAsync(context, "dbo.tx_target");
        await context.ExecuteAsync("CREATE TABLE dbo.tx_target (id int NOT NULL PRIMARY KEY, geom geometry NULL)");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);

        var transaction = await context.Store.BeginAsync();
        await context.Store.WriteAsync("dbo.tx_target", new FeatureBatch(schema, [PointFeature("1", null, 1, 2, 4326, schema)]), transaction);

        Assert.True(await context.Store.RollbackAsync(transaction));
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM dbo.tx_target"));
    }

    [SkippableFact]
    public async Task A_write_failure_keeps_the_implicit_transaction_pure()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await DropAsync(context, "dbo.atomic_target");
        await context.ExecuteAsync("CREATE TABLE dbo.atomic_target (id int NOT NULL PRIMARY KEY, geom geometry NULL)");
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var batch = new FeatureBatch(schema, [PointFeature("1", null, 1, 2, 4326, schema), PointFeature("1", null, 1, 2, 4326, schema)]);

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.WriteAsync("dbo.atomic_target", batch));
        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        Assert.Equal(0, await context.CountAsync("SELECT count(*) FROM dbo.atomic_target"));
    }

    [SkippableFact]
    public async Task Cancelling_a_scan_throws_operation_cancelled()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Store.ScanAsync("dbo.bigpoints", cancellation.Token));
    }

    [SkippableFact]
    public async Task Diagnostics_never_leak_the_connection_secret()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        var secret = $"Str0ng-{Guid.NewGuid():N}";
        var broken = new SqlServerStore(new SqlServerOptions
        {
            ConnectionString = $"Server=localhost,1433;Database=spatial;User ID=sa;Password={secret};Encrypt=False;TrustServerCertificate=True;Connect Timeout=2",
        });
        await using (broken)
        {
            var exception = await Assert.ThrowsAsync<SpatialException>(() => broken.ListAsync());

            Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
            Assert.DoesNotContain(secret, exception.Message);
        }
    }

    [SkippableFact]
    public async Task An_unconfigured_store_is_store_unavailable()
    {
        var unconfigured = new SqlServerStore(new SqlServerOptions { ConnectionString = string.Empty });
        await using (unconfigured)
        {
            var failure = await Assert.ThrowsAsync<SpatialException>(() => unconfigured.ListAsync());

            Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
            Assert.Contains(SqlServerOptions.EnvironmentVariable, failure.Message);
        }
    }

    [SkippableFact]
    public async Task An_invalid_dataset_identifier_is_invalid_arguments()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Store.ScanAsync("places; DROP TABLE places"));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Lookup_by_identity_returns_matching_features_and_ignores_misses()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var found = await context.Store.GetAsync(
            "dbo.places", [new FeatureId("1"), new FeatureId("3"), new FeatureId("404")]);

        Assert.Equal(["1", "3"], found.Select(feature => feature.Id.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        var berlin = found.Single(feature => feature.Id.Value == "1");
        Assert.Equal("Berlin", berlin["name"].StringValue);
        Assert.Equal(GeometryType.Point, berlin[2].GeometryValue.Type);
    }

    [SkippableFact]
    public async Task Lookup_by_identity_of_a_missing_feature_is_empty()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        Assert.Empty(await context.Store.GetAsync("dbo.places", [new FeatureId("404")]));
    }

    [SkippableFact]
    public async Task Lookup_by_identity_of_a_table_without_a_primary_key_is_empty()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID('dbo.unkeyed', 'U') IS NOT NULL DROP TABLE dbo.unkeyed; "
            + "CREATE TABLE dbo.unkeyed (kind nvarchar(20) NULL, geom geometry NOT NULL);");

        Assert.Empty(await context.Store.GetAsync("dbo.unkeyed", [new FeatureId("1")]));
    }

    [SkippableFact]
    public async Task Lookup_by_a_malformed_identity_is_rejected()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Store.GetAsync("dbo.places", [new FeatureId("1|2")]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    private static async Task DropAsync(SqlServerTestContext context, string table) =>
        await context.ExecuteAsync($"IF OBJECT_ID('{table}', 'U') IS NOT NULL DROP TABLE {table};");

    /// <summary>
    /// One point feature shaped by the caller's schema, so a test can use it
    /// with or without the optional name column.
    /// </summary>
    private static Feature PointFeature(
        string id, string? name, double x, double y, int srid, FeatureSchema schema) =>
        new(new FeatureId(id), schema, schema.Fields.Select(field => Value(field, name, id, x, y, srid)).ToArray());

    private static AttributeValue Value(
        FieldDefinition field, string? name, string id, double x, double y, int srid) => field.Name switch
        {
            "id" => AttributeValue.FromInt64(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)),
            "name" => name is null ? AttributeValue.Null : AttributeValue.FromString(name),
            _ => AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(srid))),
        };
}
