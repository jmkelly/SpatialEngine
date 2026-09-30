using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;
using CoreBoundingBox = Spatial.Contracts.BoundingBox;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The indexes a created dataset carries (ADR-0092): the spatial index on the geometry column the bounding-box
/// pushdown filters, and the btree indexes on the attribute columns a
/// pushed-down filter may name. Without them the pushdown query scans the
/// table. The plan assertion here runs the statement the store itself builds
/// (<see cref="SqlServerPredicateSql"/> + <see cref="SqlServerQueries"/>) against a
/// real SQL Server container. Skips with an explicit reason without Docker.
/// </summary>
public sealed class SqlServerIndexIntegrationTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerIndexIntegrationTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    private static string Unique(string prefix) => $"dbo.{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static FeatureSchema Schema(params (string Name, AttributeKind Kind)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, nullable: false)));

    [SkippableFact]
    public async Task Create_indexes_the_attribute_columns_the_server_can_key()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_create");
        var schema = Schema(
            ("name", AttributeKind.String),
            ("population", AttributeKind.Int64),
            ("geom", AttributeKind.Geometry));

        await context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);

        var names = await IndexNamesAsync(context, dataset);
        Assert.True(await HasIndexAsync(context, dataset, "population", spatial: false), string.Join(", ", names));
        // The two limits SQL Server puts on this plan (ADR-0092), both verified
        // against the container: a text column is created as nvarchar(max) and
        // cannot be an index key. The other limit — a table with no clustered
        // primary key cannot be gridded — is the engine key ADR-0147 added to
        // a created table, so the created dataset is gridded after all.
        Assert.False(await HasIndexAsync(context, dataset, "name", spatial: false), string.Join(", ", names));
        Assert.True(await HasIndexAsync(context, dataset, "geom", spatial: true), string.Join(", ", names));
    }

    /// <summary>
    /// The engine key the created table is clustered on exists for the spatial
    /// index alone: it is on the table, and the contract does not see it — a
    /// created dataset is the keyless dataset ADR-0131 and ADR-0140 are written
    /// against (ADR-0147).
    /// </summary>
    [SkippableFact]
    public async Task A_created_dataset_is_clustered_on_the_engine_key_and_does_not_expose_it()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_key");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));

        await context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);

        var names = await IndexNamesAsync(context, dataset);
        Assert.Contains(SqlServerQueries.EngineKeyConstraint(Parse(dataset)), names);
        var description = await context.Store.DescribeAsync(dataset);
        Assert.Empty(description.IdColumns);
        Assert.Equal(
            ["population", "geom"],
            description.Schema.Fields.Select(field => field.Name).ToArray());
    }

    /// <summary>
    /// The whole point of the engine key: the pushdown over a created dataset
    /// seeks the spatial index instead of reading the clustered index.
    /// </summary>
    [SkippableFact]
    public async Task A_created_dataset_pushdown_seeks_the_spatial_index()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_created_plan");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));

        await context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);
        await SeedAsync(context, dataset, 20000);

        var plan = Plan(await ExplainPushdownAsync(context, dataset, new CoreBoundingBox(0, 0, 1, 1), "population > 100000"));

        Assert.Contains($"ix_{Parse(dataset).Table}_geom", plan, StringComparison.Ordinal);
        Assert.Contains("Seek", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Table Scan", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Clustered Index Scan", plan, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_keyed_ingest_is_gridded_and_indexes_its_numeric_columns()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_ingest");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(7), Point(13.4, 52.5)])]);

        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), [page]);

        var names = string.Join(", ", await IndexNamesAsync(context, dataset));
        Assert.True(await HasIndexAsync(context, dataset, "geom", spatial: true), names);
        Assert.True(await HasIndexAsync(context, dataset, "population", spatial: false), names);
    }

    [SkippableFact]
    public async Task A_keyed_ingest_does_not_index_the_primary_key_a_second_time()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_keyed");
        var schema = Schema(("code", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1), Point(1, 2)])]);

        await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), [page]);

        var names = await IndexNamesAsync(context, dataset);
        Assert.Contains(names, name => name.StartsWith("PK_", StringComparison.Ordinal));
        Assert.DoesNotContain($"ix_{Parse(dataset).Table}_code", names);
    }

    [SkippableFact]
    public async Task The_bounding_box_pushdown_seeks_the_spatial_index_and_does_not_scan_the_table()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_plan");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(7), Point(1, 2)])]);
        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), [page]);
        await SeedAsync(context, dataset, 20000);

        var plan = Plan(await ExplainPushdownAsync(context, dataset, new CoreBoundingBox(0, 0, 1, 1), "population > 100000"));

        Assert.Contains($"ix_{Parse(dataset).Table}_geom", plan, StringComparison.Ordinal);
        Assert.Contains("Seek", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Table Scan", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Clustered Index Scan", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the plan: a filter that is selective on its own has to
    /// reach the attribute index too, not just the spatial one.
    /// </summary>
    [SkippableFact]
    public async Task A_selective_attribute_filter_seeks_the_attribute_index()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_attrplan");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        var page = new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(7), Point(1, 2)])]);
        await context.Ingest.IngestAsync(new IngestRequest(dataset, 4326), [page]);
        await SeedAsync(context, dataset, 20000);

        // One row in twenty thousand: a seek and a key lookup beat reading the
        // clustered index, so the plan has to name the attribute index.
        var plan = Plan(await ExplainPushdownAsync(context, dataset, null, "population > 199990"));

        Assert.Contains($"ix_{Parse(dataset).Table}_population", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Table Scan", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Clustered Index Scan", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Table Scan", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// An index the server refuses must fail the whole create rather than
    /// leave an unindexed table behind. The failure is injected with a
    /// database DDL trigger that refuses every <c>CREATE INDEX</c> while it is
    /// enabled, and is disabled again in the <c>finally</c> so the rest of the
    /// suite indexes normally.
    /// </summary>
    [SkippableFact]
    public async Task An_index_that_cannot_be_created_leaves_no_dataset()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_blocked");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        await context.ExecuteAsync("CREATE TRIGGER block_index_creation ON DATABASE FOR CREATE_INDEX AS RAISERROR ('index creation is blocked by the test', 16, 1);");

        try
        {
            var failure = await Assert.ThrowsAsync<SpatialException>(
                () => context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326));

            Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
            Assert.Equal(0, await TableCountAsync(context, dataset));
        }
        finally
        {
            await context.ExecuteAsync("DISABLE TRIGGER block_index_creation ON DATABASE");
        }
    }

    [SkippableFact]
    public async Task A_cancelled_create_leaves_no_dataset()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("indexed_cancel");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326, cancellation.Token));

        Assert.Equal(0, await TableCountAsync(context, dataset));
    }

    [SkippableFact]
    public async Task Index_creation_can_be_turned_off_for_a_bulk_load()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        var dataset = Unique("indexed_off");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        await using var store = new SqlServerStore(new SqlServerOptions
        {
            ConnectionString = _fixture.ConnectionString,
            CreateIndexes = false,
        });
        await store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);

        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        Assert.Empty(await IndexNamesAsync(context, dataset));
    }

    /// <summary>
    /// The opt-out is a whole-DDL opt-out (ADR-0147): with index creation off
    /// there is no spatial index to grid, so the engine key is not added either
    /// and the created table is the one ADR-0092 shipped.
    /// </summary>
    [SkippableFact]
    public async Task A_created_dataset_without_index_creation_is_not_clustered_on_an_engine_key()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        var dataset = Unique("indexed_off_key");
        var schema = Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry));
        await using (var store = new SqlServerStore(new SqlServerOptions
        {
            ConnectionString = _fixture.ConnectionString,
            CreateIndexes = false,
        }))
        {
            await store.CreateAsync(dataset, new FeatureBatch(schema, []), 4326);
        }

        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var names = await IndexNamesAsync(context, dataset);
        Assert.DoesNotContain(names, name => name.StartsWith("spatial_key_", StringComparison.Ordinal));
        var description = await context.Store.DescribeAsync(dataset);
        Assert.Empty(description.IdColumns);
    }

    private static AttributeValue Point(double x, double y) =>
        AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326)));

    /// <summary>The index names of one dataset, as the SQL Server catalogue reports them.</summary>
    private static Task<string[]> IndexNamesAsync(SqlServerTestContext context, string dataset)
    {
        var parsed = Parse(dataset);
        return StringsAsync(
            context,
            "SELECT i.name FROM sys.indexes i "
            + "JOIN sys.tables t ON t.object_id = i.object_id "
            + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @p0 AND t.name = @p1 AND i.name IS NOT NULL",
            [parsed.Schema, parsed.Table]);
    }

    /// <summary>
    /// Whether one dataset carries the index this provider names for a column:
    /// a btree (<c>type = 1</c>) for an attribute column, a spatial index
    /// (<c>type = 4</c>) for the geometry column.
    /// </summary>
    private static async Task<bool> HasIndexAsync(
        SqlServerTestContext context, string dataset, string column, bool spatial)
    {
        var parsed = Parse(dataset);
        var rows = await StringsAsync(
            context,
            "SELECT i.name FROM sys.indexes i "
            + "JOIN sys.tables t ON t.object_id = i.object_id "
            + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + $"WHERE s.name = @p0 AND t.name = @p1 AND c.name = @p2 AND i.type {(spatial ? "= 4" : "IN (1, 2)")}",
            [parsed.Schema, parsed.Table, column]);
        return rows.Any(name => string.Equals(name, $"ix_{parsed.Table}_{column}", StringComparison.Ordinal));
    }

    private static Task<int> TableCountAsync(SqlServerTestContext context, string dataset) =>
        CountAsync(
            context,
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @p0 AND t.name = @p1",
            [Parse(dataset).Schema, Parse(dataset).Table]);

    private static async Task<string[]> StringsAsync(SqlServerTestContext context, string sql, object?[] parameters)
    {
        await using var connection = await OpenAsync(context);
        await using var command = Build(connection, sql, parameters);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task<int> CountAsync(SqlServerTestContext context, string sql, object?[] parameters)
    {
        await using var connection = await OpenAsync(context);
        await using var command = Build(connection, sql, parameters);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SqlConnection> OpenAsync(SqlServerTestContext context)
    {
        var connection = new SqlConnection(context.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static SqlCommand Build(SqlConnection connection, string sql, object?[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i] ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>
    /// The plan of the statement the store's own pushdown builds for a bounding
    /// box and a filter, with the store's bound values, as
    /// <c>SET STATISTICS XML ON</c> reports it alongside the statement's own
    /// result set. Every set is read, because the plan arrives as its own.
    /// <para>
    /// The plan is read this way rather than through <c>SHOWPLAN</c>, which
    /// returns nothing for a parameterised statement: the store's query is sent
    /// as <c>sp_executesql</c>, and the server compiles an RPC without a plan
    /// to show. The plan cache is not a fallback either — its DMVs hide the
    /// <c>text</c> column from a login without <c>VIEW SERVER STATE</c>, which
    /// is the container the suite runs as.
    /// </para>
    /// </summary>
    private static async Task<string> ExplainPushdownAsync(
        SqlServerTestContext context, string dataset, CoreBoundingBox? bbox, string filter)
    {
        var description = await context.Store.DescribeAsync(dataset);
        Assert.True(
            FeatureFilterText.TryParse(filter, out var parsed, out var error), error);
        var parameters = new List<object?>();
        var predicate = SqlServerPredicateSql.Build(description, bbox, parsed, parameters);
        var statement = SqlServerQueries.Query(Parse(dataset), description.Schema, predicate);
        var lines = new List<string>();
        await using var connection = await OpenAsync(context);
        try
        {
            await using (var statistics = connection.CreateCommand())
            {
                statistics.CommandText = "SET STATISTICS XML ON";
                await statistics.ExecuteNonQueryAsync();
            }

            await using (var command = Build(connection, statement, [.. parameters]))
            {
                await using var reader = await command.ExecuteReaderAsync();
                do
                {
                    while (await reader.ReadAsync())
                    {
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            // The statement's own rows are features; the plan
                            // arrives as a row of its own, in its own set.
                            if (await reader.IsDBNullAsync(i))
                            {
                                continue;
                            }

                            var value = reader.GetValue(i).ToString() ?? string.Empty;
                            if (value.StartsWith("<ShowPlanXML", StringComparison.Ordinal))
                            {
                                lines.Add(value);
                            }
                        }
                    }
                }
                while (await reader.NextResultAsync());
            }
        }
        finally
        {
            await using var reset = connection.CreateCommand();
            reset.CommandText = "SET STATISTICS XML OFF";
            await reset.ExecuteNonQueryAsync();
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A showplan reduced to the tokens an assertion reads: the physical
    /// operators it ran and the indexes it named. A failure then reports the
    /// plan the server actually chose instead of a wall of XML.
    /// </summary>
    private static string Plan(string showPlan) =>
        string.Join(
            ", ",
            System.Text.RegularExpressions.Regex
                .Matches(showPlan, "PhysicalOp=\"[^\"]*\"|Index=\"[^\"]*\"")
                .Select(match => match.Value)
                .Select(value => value.StartsWith("Index", StringComparison.Ordinal) ? $"index {value[7..^2]}" : value));

    /// <summary>Fills a created dataset with a spread of points and refreshes the planner statistics.</summary>
    private static async Task SeedAsync(SqlServerTestContext context, string dataset, int rows)
    {
        await context.ExecuteAsync(
            $"""
             INSERT INTO {dataset} ([population], [geom])
             SELECT g * 10,
                    geometry::STGeomFromText(CONCAT('POINT (', ((g % 100) / 10.0) - 5, ' ', ((g / 100) / 10.0) - 5, ')'), 4326)
             FROM (SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS s
             """);
        await context.ExecuteAsync($"UPDATE STATISTICS {dataset}");
    }

    private static SqlServerDatasetName Parse(string dataset)
    {
        Assert.True(SqlServerDatasetName.TryParse(dataset, out var name, out _));
        return name;
    }
}
