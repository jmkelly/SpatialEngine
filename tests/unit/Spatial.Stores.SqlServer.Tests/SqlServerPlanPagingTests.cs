using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// Which plans the SQL Server store addresses as a <em>page</em> in T-SQL and
/// which it finishes over the whole read (ADR-0116 §1, ADR-0124). The
/// distinction is the memory claim: a plan read with <c>OFFSET</c>/<c>FETCH
/// NEXT</c> never holds more than a page, and a plan finished over a whole read
/// does.
///
/// <para>
/// The rule here is narrower than PostGIS's, for one dialect reason:
/// <c>OFFSET</c>/<c>FETCH NEXT</c> is legal in T-SQL only over an
/// <c>ORDER BY</c>, and the order it would be written over is the plan's — an
/// unordered plan's order is its scan order, and a plan ordered by a key the
/// table cannot make total has no position an <c>OFFSET</c> can name. So a
/// pushed page is an <em>ordered</em> plan whose order carries the identity
/// tie-break, and every other plan is finished by the reference executor over
/// the rows the store selected.
/// </para>
/// </summary>
public sealed class SqlServerPlanPagingTests
{
    private const string Qualified = "dbo.places";

    private static SqlServerDatasetName Dataset
    {
        get
        {
            Assert.True(SqlServerDatasetName.TryParse(Qualified, out var name, out var reason), reason);
            return name;
        }
    }

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("city", AttributeKind.String, false),
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("geom", AttributeKind.Geometry, false)]);

    /// <summary>
    /// The total order the plan reader builds over <c>id</c>: the requested
    /// key with its null placement, then the identity tie-break — the feature
    /// id string, which is what the reference breaks ties on
    /// (<see cref="SqlServerTextOrderTests"/>).
    /// </summary>
    private const string TotalOrderSql =
        "CASE WHEN [id] IS NULL THEN 1 ELSE 0 END, [id] ASC, CONVERT(nvarchar(max), [id]) COLLATE Latin1_General_100_BIN2 ASC";

    private static readonly IReadOnlyList<string> TotalOrder = [TotalOrderSql];

    [Fact]
    public void An_unrestricted_but_ordered_plan_is_pushed_as_a_page()
    {
        // The plan that was the shallow edge: nothing restricts it, so the
        // store used to fetch the whole table and page on the way in. The
        // identity order makes the page addressable, so it is a capped read.
        Assert.True(SqlServerPlanReader.Pushed(new FeatureQuery(Order: [new OrderTerm("id")]), TotalOrder));
    }

    [Fact]
    public void An_unordered_plan_is_finished_over_the_whole_read()
    {
        // No order: the plan's order is the scan order, and an OFFSET into a
        // scan order is not a position the next statement can reproduce — T-SQL
        // will not even take the clause without an ORDER BY.
        Assert.False(SqlServerPlanReader.Pushed(FeatureQuery.All, order: null));
    }

    [Fact]
    public void A_plan_whose_order_the_table_cannot_make_total_is_finished_over_the_whole_read()
    {
        Assert.False(SqlServerPlanReader.Pushed(new FeatureQuery(Order: [new OrderTerm("id")]), order: null));
    }

    [Fact]
    public void A_plan_with_several_sort_keys_is_pushed_as_a_page()
    {
        // A composite order is the contract's own order: each requested key a
        // tie-break over the ones before it, then the identity. The reference
        // applies every key that way (ADR-0127), so a pushed composite order
        // answers exactly what the reference answers — and the whole point of
        // pushing a page is that a composite order over a large layer is a
        // capped read rather than a materialisation.
        var composite = new[]
        {
            "CASE WHEN [city] IS NULL THEN 1 ELSE 0 END, [city] ASC",
            "CASE WHEN [id] IS NULL THEN 0 ELSE 1 END, [id] DESC",
            "CONVERT(nvarchar(max), [id]) COLLATE Latin1_General_100_BIN2 ASC",
        };

        Assert.True(SqlServerPlanReader.Pushed(
            new FeatureQuery(Order: [new OrderTerm("city"), new OrderTerm("id", SortDirection.Descending)]),
            composite));
    }

    [Fact]
    public void A_single_sort_key_is_pushed_however_the_database_compares_its_text()
    {
        Assert.True(SqlServerPlanReader.Pushed(
            new FeatureQuery(Order: [new OrderTerm("city")]), [TotalOrderSql]));
    }

    [Fact]
    public void An_ordered_plan_reads_a_cap_and_a_start_with_the_page_clause_t_sql_asks_for()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Read(
            Dataset,
            SqlServerPlanQueries.Columns(Schema, null),
            where: null,
            order: TotalOrder,
            paging: new SqlServerPlanQueries.Paging(20, 40),
            parameters);

        Assert.Equal(
            "SELECT [id], [city], [population], [geom].STAsBinary() FROM [dbo].[places]"
            + $" ORDER BY {TotalOrderSql} OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY",
            sql);
        Assert.Equal([40, 20], parameters);
    }

    [Fact]
    public void A_restriction_is_pushed_before_the_page_so_its_parameters_come_first()
    {
        var parameters = new List<object?> { 1000L };
        var sql = SqlServerPlanQueries.Read(
            Dataset,
            SqlServerPlanQueries.Columns(Schema, null),
            where: "[population] > @p0",
            order: TotalOrder,
            paging: new SqlServerPlanQueries.Paging(10, 0),
            parameters);

        Assert.Equal(
            "SELECT [id], [city], [population], [geom].STAsBinary() FROM [dbo].[places]"
            + $" WHERE [population] > @p0 ORDER BY {TotalOrderSql} OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            sql);
        Assert.Equal([1000L, 0, 10], parameters);
    }

    [Fact]
    public void A_start_with_no_cap_is_a_read_of_the_rest_of_the_order()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Read(
            Dataset,
            SqlServerPlanQueries.Columns(Schema, null),
            where: null,
            order: TotalOrder,
            paging: new SqlServerPlanQueries.Paging(null, 40),
            parameters);

        Assert.Equal(
            "SELECT [id], [city], [population], [geom].STAsBinary() FROM [dbo].[places]"
            + $" ORDER BY {TotalOrderSql} OFFSET @p0 ROWS",
            sql);
        Assert.Equal([40], parameters);
    }

    [Fact]
    public void A_read_with_no_cap_and_no_start_is_the_whole_table_and_writes_no_page_clause()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Read(
            Dataset,
            SqlServerPlanQueries.Columns(Schema, null),
            where: null,
            order: TotalOrder,
            paging: new SqlServerPlanQueries.Paging(null, 0),
            parameters);

        Assert.Equal(
            "SELECT [id], [city], [population], [geom].STAsBinary() FROM [dbo].[places]"
            + $" ORDER BY {TotalOrderSql}",
            sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_page_cannot_be_written_without_an_order_to_skip_over()
    {
        // The invariant the read's caller relies on, and T-SQL's own rule:
        // `OFFSET`/`FETCH NEXT` needs an `ORDER BY`. The plan reader answers
        // `null` for a plan this shape would be asked about, so the guard is
        // the local statement of a rule rather than a runtime surprise.
        var parameters = new List<object?>();

        Assert.Throws<ArgumentException>(() => SqlServerPlanQueries.Read(
            Dataset,
            SqlServerPlanQueries.Columns(Schema, null),
            where: null,
            order: null,
            paging: new SqlServerPlanQueries.Paging(10, 0),
            parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void The_count_the_page_reports_comes_from_the_database()
    {
        Assert.Equal(
            "SELECT COUNT(*) FROM [dbo].[places] WHERE [population] > @p0",
            SqlServerPlanQueries.Count(Dataset, "[population] > @p0"));
        Assert.Equal("SELECT COUNT(*) FROM [dbo].[places]", SqlServerPlanQueries.Count(Dataset, null));
    }

    [Fact]
    public void The_projection_is_the_requested_fields_in_the_requested_order()
    {
        Assert.Equal(["[city]", "[id]"], SqlServerPlanQueries.Columns(Schema, ["city", "id"]));
        Assert.Equal(
            ["[city]", "[id]", "[geom].STAsBinary()"],
            SqlServerPlanQueries.Columns(Schema, ["city", "id", "geom"]));
    }
}
