using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised ingest path (ADR-0041 §3): atomic create + load, the
/// identity modes, the omit-identity add (ADR-0043) and the rollback that
/// leaves no table behind. Skips with an explicit reason without Docker.
/// </summary>
public sealed class PostgisIngestIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisIngestIntegrationTests(PostgisContainerFixture fixture) => _fixture = fixture;

    private static string Unique(string prefix) =>
        $"public.{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

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
        AttributeKind.String => AttributeValue.FromString((string)value!),
        AttributeKind.Geometry => AttributeValue.FromGeometry((IGeometry)value!),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "unsupported test kind"),
    };

    [SkippableFact]
    public async Task Auto_ingest_creates_an_editable_dataset_atomically()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
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
    public async Task An_add_without_an_objectid_gets_the_store_assigned_identity()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_add");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326),
            [new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))])]);

        var description = await context.Store.DescribeAsync(dataset);
        var addSchema = description.Schema;
        var add = new Feature(
            FeatureId.Unassigned,
            addSchema,
            [
                AttributeValue.FromString("Rome"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(12.5, 41.9, CoordinateReference.Epsg(4326))),
                AttributeValue.FromInt64(0),
            ]);

        var outcomes = await context.Editor.AddAsync(dataset, new FeatureBatch(addSchema, [add]));

        Assert.True(outcomes[0].Succeeded, outcomes[0].ErrorMessage);
        Assert.Equal("2", outcomes[0].Id.Value);
    }

    [SkippableFact]
    public async Task A_mid_batch_failure_leaves_no_table()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_rollback");
        var schema = Schema(("code", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", 7L, GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326)))]),
            new FeatureBatch(schema, [Feature(schema, "2", 7L, GeometryFactory.CreatePoint(3, 4, CoordinateReference.Epsg(4326)))]),
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), pages));

        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync(dataset));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task A_duplicate_dataset_is_rejected()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_dup");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]),
        };
        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Ingest_keeps_a_mixed_case_field_name_verbatim()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_case");
        var schema = Schema(
            ("LABELRANK", AttributeKind.String),
            ("magType", AttributeKind.String),
            ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "5", "ml", GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var description = await context.Store.DescribeAsync(dataset);
        Assert.Contains(description.Schema.Fields, field => field.Name == "LABELRANK");
        var feature = (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Single();
        Assert.Equal("5", feature["LABELRANK"].StringValue);
        Assert.Equal("ml", feature["magType"].StringValue);

        // The quoted name also resolves as a filter column.
        var filtered = (await context.Store.QueryAsync(dataset, new FeatureQuery(Where: FeatureFilter.Parse("LABELRANK = '5'")))).Batches;
        Assert.Single(filtered.SelectMany(batch => batch.Features));
    }

    [SkippableFact]
    public async Task Ingest_preserves_the_z_ordinate()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_z");
        var table = dataset[(dataset.IndexOf('.') + 1)..];
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, 34.5, CoordinateReference.Epsg(4326)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        var feature = (await context.Store.ScanAsync(dataset)).SelectMany(batch => batch.Features).Single();
        var point = Assert.IsType<Point>(feature["geom"].GeometryValue);
        Assert.Equal(34.5, (double)point.Z!, precision: 12);
        Assert.Equal(3, await context.CountAsync(
            $"SELECT coord_dimension FROM geometry_columns WHERE f_table_name = '{table}'"));
    }

    [SkippableFact]
    public async Task A_3d_ingest_is_described_as_3d()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_z_describe");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, 34.5, CoordinateReference.Epsg(4326)))]),
        };

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages);

        // The typmod the batch's own coordinate layout resolved to is the one the
        // description reads back, so a 3D load is not advertised as 2D.
        Assert.Equal(CoordinateLayout.Xyz, (await context.Store.DescribeAsync(dataset)).GeometryLayout);
    }

    [SkippableFact]
    public async Task A_stream_whose_later_page_drops_the_z_is_rejected()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream_mixed");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var pages = new[]
        {
            new FeatureBatch(
                schema,
                [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, 34.5, CoordinateReference.Epsg(4326)))]),
            new FeatureBatch(
                schema,
                [Feature(schema, "2", "Paris", GeometryFactory.CreatePoint(2.35, 48.85, CoordinateReference.Epsg(4326)))]),
        };

        // The first page declares geometry(PointZ, 4326); a later 2D page cannot
        // be stored in it, so the load is refused as bad input rather than
        // surfacing as an opaque driver failure.
        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, ToStream(pages)));

        Assert.Equal("invalid.arguments", failure.Code);
        Assert.Contains("use one coordinate layout per dataset", failure.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync(dataset));
    }

    [SkippableFact]
    public async Task A_pre_cancelled_ingest_throws_operation_cancelled()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
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

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), pages, cancellation.Token));
    }

    /// <summary>
    /// The streaming face (ADR-0041 §4): the same atomic load with the pages
    /// arriving as they are decoded. The failure cases are the ones that
    /// matter — a stream that dies part-way through must leave no table.
    /// </summary>
    [SkippableFact]
    public async Task Streamed_pages_load_atomically()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
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
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream_fail");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var good = new FeatureBatch(
            schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]);

        // The store wraps anything its operation throws as `store.unavailable`,
        // decoder failure included; what matters here is what it left behind.
        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, Failing(good)));

        Assert.Contains("the decoder died", failure.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync(dataset));
    }

    [SkippableFact]
    public async Task A_cancelled_stream_leaves_no_table()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingest_stream_cancel");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var good = new FeatureBatch(
            schema, [Feature(schema, "1", "Berlin", GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326)))]);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Ingest.IngestStreamAsync(
            new IngestRequest(dataset, 4326), schema, Cancelling(good, cancellation), cancellation.Token));

        await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync(dataset));
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
