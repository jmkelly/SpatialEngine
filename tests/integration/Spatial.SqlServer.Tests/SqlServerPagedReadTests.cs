using System.Globalization;
using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The paged read of a large layer against a real SQL Server (ADR-0116 §1,
/// ADR-0124): a plan whose order the table can make total is read with
/// <c>OFFSET</c>/<c>FETCH NEXT</c> rather than fetched whole and paged on the
/// way in, and the walk a client performs over it terminates with every row
/// exactly once, in the reference's order.
///
/// <para>
/// The memory claim is pinned where it can be measured honestly: the shape of
/// the statement the store issues is pinned in <c>SqlServerPlanPagingTests</c>
/// (a capped read of a plan carries an <c>ORDER BY</c> and a
/// <c>OFFSET</c>/<c>FETCH NEXT</c>), and here the walk itself is pinned against
/// a real database. It is deliberately not measured in bytes: every read used to
/// re-read the dataset description from the catalogue, and that fixed cost was
/// two orders of magnitude larger than the difference between a page and a
/// whole table, so the instrument would have measured the catalogue, not the
/// read. That per-read discovery is gone (ADR-0151), and what the walk now
/// costs is measured as a count in
/// <c>Spatial.SqlServer.Tests.SqlServerDescriptionCacheTests</c>.
/// </para>
/// </summary>
public sealed class SqlServerPagedReadTests : IClassFixture<SqlServerContainerFixture>
{
    private const int Rows = 2_000;
    private const int PageSize = 100;

    private readonly SqlServerContainerFixture _fixture;

    public SqlServerPagedReadTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature Row(long id) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString("row"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id % 180, (id % 90) - 45)),
        ]);

    /// <summary>
    /// The table is created by hand rather than through the store's
    /// <c>CreateAsync</c>, because the pushdown is decided by the dataset's
    /// identity: a table with a primary key is one whose order can be made
    /// total, and <c>CreateAsync</c> creates a table without one. The rows are
    /// written through the store, so the write path is the store's.
    /// </summary>
    private async Task<string> SeededAsync()
    {
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.paged_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id bigint NOT NULL PRIMARY KEY, name nvarchar(100) NULL, geometry geometry NULL)");
        var batch = new List<Feature>(500);
        for (var id = 1L; id <= Rows; id++)
        {
            batch.Add(Row(id));
            if (batch.Count == 500)
            {
                await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, batch));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, batch));
        }

        return dataset;
    }

    [SkippableFact]
    public async Task A_paged_walk_over_a_large_layer_terminates_with_every_row_once_in_order()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();

        var seen = new List<long>(Rows);
        var query = new FeatureQuery(Order: [new OrderTerm("id")]) with { Limit = PageSize };
        var pages = 0;
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            Assert.True(read.Features.Count() <= PageSize);
            seen.AddRange(read.Features.Select(feature => feature["id"].Int64Value));
            pages++;
            if (!read.HasMore)
            {
                Assert.Null(read.NextCursor);
                break;
            }

            Assert.NotNull(read.NextCursor);
            query = query with { Cursor = read.NextCursor, Offset = null };
            Assert.True(pages <= (Rows / PageSize) + 1, "the walk did not terminate");
        }

        Assert.Equal(Rows, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(Rows, seen.Distinct().Count());
        Assert.Equal((Rows / PageSize) + (Rows % PageSize == 0 ? 0 : 1), pages);
    }

    /// <summary>
    /// The exact total a <c>returnCountOnly</c> request reports is the same
    /// number the paged walk collects: the count and the page are the same
    /// question asked two ways.
    /// </summary>
    [SkippableFact]
    public async Task The_paged_walk_collects_exactly_what_the_count_reports()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();
        var clause = new Predicate.Compare(new FieldRef("name"), ComparisonOperator.Equals, Literal.FromText("row"));

        var total = await context.Store.CountAsync(dataset, new FeatureQuery(Where: clause, Order: [new OrderTerm("id")]));

        var seen = 0;
        var query = new FeatureQuery(Where: clause, Order: [new OrderTerm("id")]) with { Limit = PageSize };
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            Assert.Equal(total, read.TotalCount);
            seen += read.Features.Count();
            if (!read.HasMore)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        Assert.Equal(Rows, total);
        Assert.Equal(total, seen);
    }

    /// <summary>
    /// A cursor the store did not issue for this plan is a typed
    /// <c>invalid.arguments</c>, whether the plan was pushed or finished
    /// (ADR-0074 §5).
    /// </summary>
    [SkippableFact]
    public async Task A_cursor_this_plan_did_not_issue_is_refused()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Store.QueryAsync(
            dataset,
            new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize) with { Cursor = "v1.0.deadbeef" }));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task A_cancelled_paged_read_cancels_rather_than_answers()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // The exception type is the whole of the promise here: SqlClient links
        // the caller's token into its own, so the token on the thrown
        // OperationCanceledException is not the caller's (the store's own
        // cancellation tests assert the same).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Store.QueryAsync(
            dataset,
            new FeatureQuery(Order: [new OrderTerm("id")]) with { Limit = PageSize },
            cancellation.Token));
    }

    /// <summary>
    /// A paged walk over a <em>text</em> key, which is the case a locale
    /// collation gets wrong and a page boundary makes visible (ADR-0121): the
    /// rows come back <c>Alpha, Charlie, _charlie, a-delta, bravo</c> — the
    /// contract's ordinal order, not the <c>SQL_Latin1_General_CP1_CI_AS</c>
    /// order the container's default collation gives.
    /// </summary>
    [SkippableFact]
    public async Task A_paged_walk_over_a_text_key_is_the_ordinal_order()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.text_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id bigint NOT NULL PRIMARY KEY, label nvarchar(100) NULL, geometry geometry NULL); "
            + $"INSERT INTO {dataset} (id, label, geometry) VALUES "
            + "(1, 'bravo', geometry::STGeomFromText('POINT (1 1)', 4326)), "
            + "(2, 'Alpha', geometry::STGeomFromText('POINT (2 2)', 4326)), "
            + "(3, '_charlie', geometry::STGeomFromText('POINT (3 3)', 4326)), "
            + "(4, 'a-delta', geometry::STGeomFromText('POINT (4 4)', 4326)), "
            + "(5, 'Charlie', geometry::STGeomFromText('POINT (5 5)', 4326)), "
            + "(6, NULL, geometry::STGeomFromText('POINT (6 6)', 4326))");

        var seen = new List<string?>();
        var query = new FeatureQuery(Order: [new OrderTerm("label")]) with { Limit = 2 };
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            Assert.Equal(6, read.TotalCount);
            seen.AddRange(read.Features.Select(feature => feature["label"].IsNull ? null : feature["label"].StringValue));
            if (!read.HasMore)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        // Nulls last ascending, upper case before lower case, punctuation
        // between the letters it sorts beside.
        Assert.Equal(["Alpha", "Charlie", "_charlie", "a-delta", "bravo", null], seen);

        // The same rows under the database's own collation, which is the answer
        // this store must *not* return: the pushed sort key names its own
        // collation, so the answer above is not the container's. Without that
        // term the two would be one answer and this test would measure
        // nothing (ADR-0121, ADR-0124).
        var collation = (string)(await ScalarAsync(
            context, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"))!;
        Assert.DoesNotContain("_BIN2", collation, StringComparison.OrdinalIgnoreCase);
        var native = await LabelsAsync(context, $"SELECT TOP 6 label FROM {dataset} ORDER BY label");
        Assert.NotEqual(seen, native);
    }

    private static async Task<object?> ScalarAsync(SqlServerTestContext context, string sql)
    {
        await using var connection = new SqlConnection(context.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<string?>> LabelsAsync(SqlServerTestContext context, string sql)
    {
        await using var connection = new SqlConnection(context.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var labels = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            labels.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return labels;
    }

    /// <summary>
    /// A table the store did not create: a <c>text</c> column, which T-SQL
    /// cannot compare or sort at all ("the text, ntext and image data types
    /// cannot be compared or sorted"). A pushed sort key over one is read
    /// through a conversion, so the plan pages rather than failing — and the
    /// page it returns is the reference's, ordinally ordered
    /// (<c>A, A, _c, a, a</c>, not the collation's order).
    /// </summary>
    [SkippableFact]
    public async Task A_pushed_sort_key_over_a_legacy_text_column_is_the_reference_order()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var table = $"legacy_{Guid.NewGuid().ToString("N")[..8]}";
        var dataset = $"dbo.{table}";
        await context.ExecuteAsync(
            $"CREATE TABLE dbo.{table} (id int NOT NULL PRIMARY KEY, label text NULL, geom geometry NULL); "
            + "INSERT INTO dbo." + table + " (id, label, geom) VALUES "
            + "(1, 'a', geometry::STGeomFromText('POINT (1 1)', 4326)), "
            + "(2, 'A', geometry::STGeomFromText('POINT (2 2)', 4326)), "
            + "(3, '_c', geometry::STGeomFromText('POINT (3 3)', 4326)), "
            + "(4, 'A', geometry::STGeomFromText('POINT (4 4)', 4326)), "
            + "(5, 'a', geometry::STGeomFromText('POINT (5 5)', 4326))");

        var page = await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("label")]));

        Assert.Equal(["2", "4", "3", "1", "5"], page.Features.Select(feature => feature.Id.Value).ToArray());
        Assert.Equal(["A", "A", "_c", "a", "a"], page.Features.Select(feature => feature["label"].StringValue).ToArray());
    }
}
