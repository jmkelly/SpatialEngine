using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

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
}
