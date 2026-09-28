using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;
using CoreBoundingBox = Spatial.Contracts.BoundingBox;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The indexes a created dataset carries (ADR-0081): the spatial index on the
/// geometry column the bounding-box pushdown filters, and the btree indexes
/// on the attribute columns a pushed-down filter may name. Without them the
/// pushdown query is a sequential scan, which is what the measurement in
/// <c>eng/spike-u2x-query-baseline/RESULTS.md</c> (finding 7) measured. The
/// plan assertions here run the statement the store itself builds
/// (<see cref="PostgisPredicate"/> + <see cref="PostgisQueries"/>) through
/// <c>EXPLAIN</c> against a real PostGIS container. Skips with an explicit
/// reason without Docker.
/// </summary>
public sealed class PostgisIndexIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisIndexIntegrationTests(PostgisContainerFixture fixture) => _fixture = fixture;

    private static string Unique(string prefix) => $"public.{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static FeatureSchema Schema(params (string Name, AttributeKind Kind)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, nullable: false)));

    [SkippableFact]
    public async Task Create_indexes_the_geometry_column_and_every_attribute_column()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_create");
        var schema = Schema(
            ("name", AttributeKind.String),
            ("population", AttributeKind.Int64),
            ("geom", AttributeKind.Geometry));

        await context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);

        var indexes = await IndexDefinitionsAsync(context, dataset);
        var table = Parse(dataset).Table;
        Assert.True(HasIndex(indexes, table, "geom", spatial: true), string.Join("; ", indexes));
        Assert.True(HasIndex(indexes, table, "name", spatial: false), string.Join("; ", indexes));
        Assert.True(HasIndex(indexes, table, "population", spatial: false), string.Join("; ", indexes));
    }

    [SkippableFact]
    public async Task The_pushed_down_query_is_an_index_scan_and_not_a_sequential_scan()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_plan");
        var schema = Schema(
            ("name", AttributeKind.String),
            ("population", AttributeKind.Int64),
            ("geom", AttributeKind.Geometry));
        await context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);
        await SeedAsync(context, dataset, 5000);

        var plan = await ExplainPushdownAsync(context, dataset, new CoreBoundingBox(0, 0, 1, 1), "population > 20000");

        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
        Assert.Contains("Index", plan, StringComparison.Ordinal);
        Assert.Contains("ix_", plan, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Ingest_creates_the_same_indexes()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_ingest");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(
            schema,
            [new Feature(
                new FeatureId("1"),
                schema,
                [AttributeValue.FromString("Berlin"), Point(13.4, 52.5)])]);

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), [page]);

        var indexes = await IndexDefinitionsAsync(context, dataset);
        var table = Parse(dataset).Table;
        Assert.True(HasIndex(indexes, table, "geom", spatial: true), string.Join("; ", indexes));
        Assert.True(HasIndex(indexes, table, "name", spatial: false), string.Join("; ", indexes));
    }

    [SkippableFact]
    public async Task A_keyed_ingest_does_not_index_the_primary_key_a_second_time()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_keyed");
        var schema = Schema(("code", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(
            schema,
            [new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1), Point(1, 2)])]);

        await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), [page]);

        Assert.DoesNotContain(
            await IndexDefinitionsAsync(context, dataset),
            index => index.Contains("(\"code\")", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_index_that_cannot_be_created_leaves_no_dataset()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_conflict");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        // A decoy table already holding the index name this dataset would take:
        // PostgreSQL index names are schema-scoped, so the create cannot
        // silently continue without the index.
        var decoy = Unique("indexed_decoy");
        await context.ExecuteAsync($"CREATE TABLE {decoy} (\"geom\" geometry(Point, 4326))");
        await context.ExecuteAsync(
            PostgisIndexPlan.CreateIndexes(Parse(dataset), schema)[0]
                .Replace(Qualified(Parse(dataset)), Qualified(Parse(decoy)), StringComparison.Ordinal));

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Equal(0, await TableCountAsync(context, dataset));
    }

    [SkippableFact]
    public async Task A_cancelled_create_leaves_no_dataset()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_cancel");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326, cancellation.Token));

        Assert.Equal(0, await TableCountAsync(context, dataset));
    }

    [SkippableFact]
    public async Task Index_creation_can_be_turned_off_for_a_bulk_load()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var dataset = Unique("indexed_off");
        var schema = Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry));
        await using var store = new PostgisStore(new PostgisOptions
        {
            ConnectionString = _fixture.ConnectionString,
            CreateIndexes = false,
        });
        await store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);

        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        Assert.Empty(await IndexDefinitionsAsync(context, dataset));
    }

    /// <summary>Whether one dataset carries the index this provider names for a column.</summary>
    private static bool HasIndex(IEnumerable<string> indexes, string table, string column, bool spatial) =>
        indexes.Any(index => index.Contains($"ix_{table}_{column} ", StringComparison.Ordinal)
            && index.Contains(spatial ? "USING gist" : "USING btree", StringComparison.OrdinalIgnoreCase));

    private static AttributeValue Point(double x, double y) =>
        AttributeValue.FromGeometry(
            GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326)));

    private static Task<int> TableCountAsync(PostgisTestContext context, string dataset) =>
        context.CountAsync(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{Parse(dataset).Table}'");


    private static async Task<string[]> IndexDefinitionsAsync(PostgisTestContext context, string dataset)
    {
        await using var dataSource = NpgsqlDataSource.Create(context.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexdef FROM pg_indexes WHERE schemaname = @p0 AND tablename = @p1";
        command.Parameters.AddWithValue("p0", Parse(dataset).Schema);
        command.Parameters.AddWithValue("p1", Parse(dataset).Table);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    /// <summary>
    /// The <c>EXPLAIN</c> of the statement the store's own pushdown builds for a
    /// bounding box and a filter, with the store's bound values.
    /// </summary>
    private static async Task<string> ExplainPushdownAsync(
        PostgisTestContext context, string dataset, CoreBoundingBox bbox, string filter)
    {
        var description = await context.Store.DescribeAsync(dataset);
        var parameters = new List<object?>();
        var predicate = PostgisPredicate.Build(description, bbox, filter, parameters);
        var statement = PostgisQueries.Query(Parse(dataset), description.Schema, predicate);
        await using var dataSource = NpgsqlDataSource.Create(context.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (COSTS OFF) {statement}";
        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i] ?? DBNull.Value);
        }

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Fills a created dataset with a spread of points and refreshes the planner statistics.</summary>
    private static async Task SeedAsync(PostgisTestContext context, string dataset, int rows)
    {
        await context.ExecuteAsync(
            $"""
             INSERT INTO {dataset} ("name", "population", "geom")
             SELECT 'city ' || g, g * 10,
                    ST_SetSRID(ST_MakePoint(((g % 100) / 10.0) - 5, ((g / 100) / 10.0) - 5), 4326)
             FROM generate_series(1, {rows}) AS g
             """);
        await context.ExecuteAsync($"ANALYZE {dataset}");
    }

    private static string Qualified(PostgisDatasetName dataset) => $"\"{dataset.Schema}\".\"{dataset.Table}\"";

    private static PostgisDatasetName Parse(string dataset)
    {
        Assert.True(PostgisDatasetName.TryParse(dataset, out var name, out _));
        return name;
    }
}
