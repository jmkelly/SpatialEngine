using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The containerised ingest path (ADR-0041 §3 as the SQL Server provider
/// follows it): atomic create + load, the identity modes, the omit-identity
/// add (ADR-0043) and the rollback that leaves no table behind. Unlike
/// PostGIS, SQL Server spatial columns are XY only, so a Z-bearing ingest is
/// refused rather than flattened. Skips with an explicit reason without
/// Docker.
/// </summary>
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerIngestIntegrationTests : IClassFixture<SqlServerDatabaseFixture>
{
    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerIngestIntegrationTests(SqlServerDatabaseFixture fixture) => _fixture = fixture;

    private static string Unique(string prefix) => $"dbo.{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static FeatureSchema Schema(params (string Name, AttributeKind Kind)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, nullable: false)));

    private static Feature Feature(FeatureSchema schema, string id, params object?[] values) =>
        new(
            new FeatureId(id),
            schema,
            schema.Fields.Select((field, index) => Value(field, values[index])).ToArray());

    private static AttributeValue Value(FieldDefinition field, object? value) => field.Kind switch
    {
        AttributeKind.Int64 => AttributeValue.FromInt64((long)value!),
        AttributeKind.Double => AttributeValue.FromDouble((double)value!),
        AttributeKind.String => AttributeValue.FromString((string)value!),
        AttributeKind.Geometry => AttributeValue.FromGeometry((IGeometry)value!),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "unsupported test kind"),
    };

    [SkippableFact]
    public async Task Auto_ingest_creates_an_editable_dataset_atomically()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_auto");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
            new FeatureBatch(schema, [Feature(schema, "2", "Paris", GeometryFactory.CreatePoint(2.35, 48.85, CoordinateReference.Epsg(4326)))]),
        };

        var outcome = await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        Assert.Equal(2, outcome.Features);
        Assert.Equal("id", outcome.IdentityField);
        var description = await context.Store.DescribeAsync(dataset);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(2, (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Count());
    }

    [SkippableFact]
    public async Task Ingested_srid_is_discovered_back_from_the_dataset_metadata()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_srid");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Origin", GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(3857)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 3857), pages);

        // The stored value carries the SRID, and the metadata row keeps the
        // dataset's CRS discoverable even when it is emptied.
        Assert.Equal(3857, (await context.Store.DescribeAsync(dataset)).Srid);
        await context.ExecuteAsync($"DELETE FROM {dataset}");
        Assert.Equal(3857, (await context.Store.DescribeAsync(dataset)).Srid);
    }

    [SkippableFact]
    public async Task An_add_without_an_objectid_gets_the_store_assigned_identity()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_auto_id");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var added = await context.Editor.AddAsync(dataset, new FeatureBatch(schema,
        [
            Feature(schema, FeatureId.Unassigned.Value, "Paris", GeometryFactory.CreatePoint(2.35, 48.85, CoordinateReference.Epsg(4326))),
        ]));
        Assert.True(Assert.Single(added).Succeeded);
        Assert.Equal(2, long.Parse(added[0].Id.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task An_add_may_omit_the_store_assigned_identity_column()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_add");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };
        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        // The batch carries only the source fields; the ingested identity
        // column is assigned by the database, not named by the client.
        var add = new Feature(
            FeatureId.Unassigned,
            schema,
            [
                AttributeValue.FromString("Rome"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(12.5, 41.9, CoordinateReference.Epsg(4326))),
            ]);

        var outcomes = await context.Editor.AddAsync(dataset, new FeatureBatch(schema, [add]));

        Assert.True(Assert.Single(outcomes).Succeeded, outcomes[0].ErrorMessage);
        Assert.Equal("2", outcomes[0].Id.Value);
        Assert.Equal(2, (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Count());
    }

    [SkippableFact]
    public async Task Source_ingest_keys_the_dataset_on_a_named_integer_field()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_source");
        var schema = Schema(("code", AttributeKind.Int64), ("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "7", 7L, "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };

        var outcome = await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), pages);

        Assert.Equal("code", outcome.IdentityField);
        Assert.Equal(["code"], (await context.Store.DescribeAsync(dataset)).IdColumns);
    }

    [SkippableFact]
    public async Task Every_ingest_creates_a_dataset_with_a_key()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_keyed");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };

        var outcome = await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        Assert.Equal("id", outcome.IdentityField);
        Assert.Equal(["id"], (await context.Store.DescribeAsync(dataset)).IdColumns);
    }

    [SkippableFact]
    public async Task A_mid_batch_failure_leaves_no_table()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_fail");
        var schema = Schema(("code", AttributeKind.Int64), ("name", AttributeKind.String));
        var pages = new[]
        {
            new FeatureBatch(schema,
            [
                Feature(schema, "1", 1L, "Berlin"),
                // The second row collides with the first on the source
                // identity, so the load fails after it has already written.
                Feature(schema, "2", 1L, "Paris"),
            ]),
        };

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Ingest.IngestAsync(
                new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), pages));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Equal(0, await context.CountAsync(
            $"SELECT count(*) FROM sys.tables WHERE name = '{dataset[(dataset.IndexOf('.') + 1)..]}'"));
    }

    [SkippableFact]
    public async Task A_duplicate_dataset_is_rejected()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_dupe");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };
        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("already exists", failure.Message);
    }

    [SkippableFact]
    public async Task Ingest_keeps_a_mixed_case_field_name_verbatim()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_case");
        var schema = Schema(("LABELRANK", AttributeKind.Double), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", 1.5d, GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var filtered = (await context.Store.QueryAsync(dataset, new FeatureQuery(Where: FeatureFilter.Parse("LABELRANK = 1.5")))).Batches;
        Assert.Single(filtered.SelectMany(batch => batch.Features));
    }

    [SkippableFact]
    public async Task Ingest_refuses_a_geometry_sql_server_cannot_store()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_z");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, 34.5, CoordinateReference.Epsg(4326)))]),
        };

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("XYZ", failure.Message);
        Assert.Equal(0, await context.CountAsync(
            $"SELECT count(*) FROM sys.tables WHERE name = '{dataset[(dataset.IndexOf('.') + 1)..]}'"));
    }

    [SkippableFact]
    public async Task A_pre_cancelled_ingest_throws_operation_cancelled()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_cancel");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326)))]),
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages, cancellation.Token));
    }

    /// <summary>
    /// The streaming face (ADR-0041 §4): the same atomic load with the pages
    /// arriving as they are decoded. A stream that dies or is cancelled
    /// part-way through must leave no table.
    /// </summary>
    [SkippableFact]
    public async Task Streamed_pages_load_atomically()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
            new FeatureBatch(schema, [Feature(schema, "2", "Paris", GeometryFactory.CreatePoint(2.35, 48.85, CoordinateReference.Epsg(4326)))]),
        };

        var outcome = await context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, ToStream(pages));

        Assert.Equal(2, outcome.Features);
        Assert.Equal(2, (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Count());
    }

    [SkippableFact]
    public async Task A_stream_that_dies_part_way_through_leaves_no_table()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream_fail");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var good = new FeatureBatch(
            schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]);

        // The store wraps anything its operation throws as `store.unavailable`,
        // decoder failure included; what matters here is what it left behind.
        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, Failing(good)));

        Assert.Contains("the decoder died", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await context.CountAsync(
            $"SELECT count(*) FROM sys.tables WHERE name = '{dataset[(dataset.IndexOf('.') + 1)..]}'"));
    }

    [SkippableFact]
    public async Task A_cancelled_stream_leaves_no_table()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream_cancel");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var good = new FeatureBatch(
            schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, Cancelling(good, cancellation), cancellation.Token));

        Assert.Equal(0, await context.CountAsync(
            $"SELECT count(*) FROM sys.tables WHERE name = '{dataset[(dataset.IndexOf('.') + 1)..]}'"));
    }

    private static async IAsyncEnumerable<FeatureBatch> ToStream(FeatureBatch[] pages)
    {
        foreach (var page in pages)
        {
            await Task.Yield();
            yield return page;
        }
    }

    /// <summary>One good page, then a failure the transaction cannot survive.</summary>
    private static async IAsyncEnumerable<FeatureBatch> Failing(FeatureBatch good)
    {
        yield return good;
        await Task.Yield();
        throw new InvalidOperationException("the decoder died");
    }

    /// <summary>One good page, then a cancellation before the next.</summary>
    private static async IAsyncEnumerable<FeatureBatch> Cancelling(FeatureBatch good, CancellationTokenSource cancellation)
    {
        yield return good;
        await cancellation.CancelAsync();
        await Task.Yield();
        cancellation.Token.ThrowIfCancellationRequested();
#pragma warning disable CS0162 // the compiler cannot see that the throw is reachable
        yield return good;
#pragma warning restore CS0162
    }
}
