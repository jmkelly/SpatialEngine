using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The shape of the SQL the store issues for a plan and for each reduction
/// (ADR-0098 §4, and the acceptance criterion this bead carries: a layer
/// answers <c>returnCountOnly</c>, a grouped <c>outStatistics</c> and a paged
/// <c>orderByFields</c> query <em>without materialising the dataset</em>).
///
/// <para>
/// "Without materialising" is a claim about the text of the query, and text is
/// what this pins: a <c>COUNT(*)</c> names no columns and reads no rows, a
/// paged read carries the cap and the start as bound values so the server stops
/// there, and a grouped reduction is a <c>GROUP BY</c> rather than a row set
/// the engine folds. The conformance suite proves the same queries answer
/// correctly; this proves they are the queries that were issued at all — a
/// dialect that quietly read every row and reduced it afterwards would pass
/// every answer-comparison and fail here.
/// </para>
///
/// <para>
/// The last case is the other half of the claim: when the store declines to
/// push something down it says so by returning <c>null</c>, and the caller
/// reduces with the reference over the rows it read. Declining is a cost, never
/// a different answer, and the shape of the refusal is the part that can rot.
/// </para>
/// </summary>
public sealed class PostgisPlanSurfaceTests
{
    private const string Qualified = "public.places";

    private static PostgisDatasetName Dataset
    {
        get
        {
            Assert.True(PostgisDatasetName.TryParse(Qualified, out var name, out var reason), reason);
            return name;
        }
    }

    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("id", AttributeKind.Int64, false),
        ("city", AttributeKind.String, false),
        ("population", AttributeKind.Int64, true),
        ("geom", AttributeKind.Geometry, false));

    [Fact]
    public void A_count_is_one_aggregate_row_and_names_no_column()
    {
        var sql = PostgisPlanQueries.Count(Dataset, "\"population\" > @p0");

        // No SELECT list: the server answers with a number, so the 34k rows
        // never cross the wire and the engine never holds them.
        Assert.Equal("SELECT COUNT(*) FROM \"public\".\"places\" WHERE \"population\" > @p0", sql);
    }

    [Fact]
    public void A_count_of_a_whole_layer_still_names_no_column()
    {
        Assert.Equal("SELECT COUNT(*) FROM \"public\".\"places\"", PostgisPlanQueries.Count(Dataset, null));
    }

    [Fact]
    public void A_paged_read_carries_the_cap_and_the_start_as_bound_values()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Read(
            Dataset,
            ["id", "city", "ST_AsEWKB(\"geom\")"],
            where: null,
            order: ["\"population\" DESC NULLS FIRST", "\"id\" ASC NULLS LAST"],
            paging: new PostgisPlanQueries.Paging(20, 40),
            parameters);

        Assert.Equal(
            "SELECT id, city, ST_AsEWKB(\"geom\") FROM \"public\".\"places\""
            + " ORDER BY \"population\" DESC NULLS FIRST, \"id\" ASC NULLS LAST"
            + " LIMIT @p0 OFFSET @p1",
            sql);
        Assert.Equal([20, 40], parameters);
    }

    [Fact]
    public void A_read_with_no_cap_is_not_paged()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Read(
            Dataset,
            PostgisPlanQueries.Columns(Schema, null),
            where: null,
            order: null,
            paging: new PostgisPlanQueries.Paging(null, 0),
            parameters);

        Assert.Equal(
            "SELECT \"id\", \"city\", \"population\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\"",
            sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_projected_read_names_only_the_projected_columns()
    {
        var columns = PostgisPlanQueries.Columns(Schema, ["city", "population"]);

        Assert.Equal(["\"city\"", "\"population\""], columns);
    }

    [Fact]
    public void A_grouped_reduction_is_a_group_by_rather_than_a_row_set()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: true,
            parameters);

        Assert.NotNull(sql);
        Assert.Contains(" GROUP BY \"city\"", sql);
        Assert.Contains(" ORDER BY \"city\" ASC NULLS LAST", sql);
        Assert.Contains("COUNT(*)", sql);
    }

    /// <summary>
    /// The envelope of a geometry column is one aggregate expression the server
    /// evaluates over the whole table (ADR-0120) — a layer's extent, which used
    /// to be a projected read of every row reduced in managed code. It is cast
    /// back to a geometry so the one EWKB reader this provider has reads it,
    /// rather than through a second reader for a box the dialect returns.
    /// </summary>
    [Fact]
    public void An_envelope_is_one_extent_expression_over_the_geometry_column()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Envelope, "geom", "box")],
            order: [],
            Schema,
            byteOrderText: false,
            parameters);

        Assert.Equal(
            "SELECT ST_AsEWKB(ST_Extent(\"geom\")::geometry) FROM \"public\".\"places\"",
            sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_grouped_reduction_with_no_order_is_declined_rather_than_invented()
    {
        // A group order the plan did not ask for is not the store's to invent:
        // SQL returns groups in no defined order, so the caller reduces the
        // rows it read with the reference, where first-seen order is knowable.
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            order: [],
            Schema,
            byteOrderText: true,
            parameters: []);

        Assert.Null(sql);
    }

    /// <summary>
    /// An ungrouped reduction is <em>one</em> group, so its row order cannot
    /// differ from the store's own whatever the plan asked for: it is a single
    /// aggregate row, and pushing it is the whole win of the shape (a table
    /// scan reduced in managed code is what this query used to cost).
    /// </summary>
    [Fact]
    public void An_ungrouped_reduction_is_one_aggregate_row_even_with_no_order()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            order: [],
            schema: Schema,
            byteOrderText: true,
            parameters);

        Assert.NotNull(sql);
        Assert.DoesNotContain(" GROUP BY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT COUNT(*) FROM \"public\".\"places\"", sql, StringComparison.Ordinal);
        Assert.Empty(parameters);
    }

    /// <summary>
    /// The <c>having</c> clause and the page over groups are the grouped
    /// statement's own <c>HAVING</c> and <c>LIMIT</c> (ADR-0128), and they are
    /// one statement rather than a wrap of it: <c>GROUP BY</c>,
    /// <c>HAVING</c>, <c>ORDER BY</c> and <c>LIMIT</c> compose in that order,
    /// so the cap cuts the groups the clause kept and the server never groups
    /// more of them than the page reports.
    ///
    /// <para>
    /// A clause over a statistic is the one name that is not a column, and it is
    /// written as the dialect's own aggregate spelling — the same expression the
    /// select list carries — because <c>HAVING</c> is evaluated per group and an
    /// aggregate is legal there. That is the only dialect-specific half of the
    /// clause; the shape of the tree, the operator and the bound literal are the
    /// predicate compiler's.
    /// </para>
    /// </summary>
    [Fact]
    public void A_group_filter_and_a_page_are_the_grouped_statements_having_and_limit()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: true,
            parameters,
            new Predicate.Compare(new FieldRef("total"), ComparisonOperator.GreaterThan, Literal.FromInteger("150")),
            new PostgisPlanQueries.Paging(10, 20));

        Assert.Equal(
            "SELECT \"city\", SUM(\"population\") FROM \"public\".\"places\" GROUP BY \"city\""
            + " HAVING SUM(\"population\") > @p0 ORDER BY \"city\" ASC NULLS LAST, \"city\" ASC NULLS LAST"
            + " LIMIT @p1 OFFSET @p2",
            sql);
        Assert.Equal([150L, 10, 20], parameters);
    }

    /// <summary>
    /// A clause over the group key is the column itself, and it states the byte
    /// order the contract compares strings in for the same reason the
    /// <c>ORDER BY</c> and the <c>WHERE</c> do (ADR-0121, ADR-0123): a locale
    /// collation would make a pushed filter a different question from the
    /// reference's.
    /// </summary>
    [Fact]
    public void A_group_filter_over_the_group_key_is_the_column_under_the_byte_order()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            parameters,
            new Predicate.IsIn(new FieldRef("city"), [Literal.FromText("Aachen"), Literal.FromText("Berlin")], Negated: false));

        Assert.Equal(
            "SELECT \"city\", COUNT(*) FROM \"public\".\"places\" GROUP BY \"city\""
            + " HAVING \"city\" COLLATE \"C\" IN (@p0, @p1)"
            + " ORDER BY \"city\" COLLATE \"C\" ASC NULLS LAST, \"city\" COLLATE \"C\" ASC NULLS LAST",
            sql);
        Assert.Equal(["Aachen", "Berlin"], parameters);
    }

    /// <summary>
    /// An ungrouped reduction is one row, so a clause over it is a <c>HAVING</c>
    /// on an aggregate with no <c>GROUP BY</c> and a page that cannot cut
    /// anything: both are written, because both are what the request asked and
    /// the reference answers the same two ways — a group that does not satisfy
    /// the clause is no group, and a page past the only group is no groups.
    /// </summary>
    [Fact]
    public void An_ungrouped_reduction_carries_its_clause_and_its_page_too()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            order: [],
            schema: Schema,
            byteOrderText: true,
            parameters,
            new Predicate.Compare(new FieldRef("total"), ComparisonOperator.GreaterThan, Literal.FromInteger("150")),
            new PostgisPlanQueries.Paging(5, 0));

        Assert.Equal(
            "SELECT SUM(\"population\") FROM \"public\".\"places\""
            + " HAVING SUM(\"population\") > @p0 LIMIT @p1",
            sql);
        Assert.Equal([150L, 5], parameters);
    }

    /// <summary>
    /// A clause over a name the group row does not carry is a question about
    /// nothing, and the store declines it rather than writing a fragment that
    /// names a column or alias that is not there.
    /// </summary>
    [Fact]
    public void A_group_filter_over_an_unknown_name_is_declined()
    {
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: true,
            parameters: [],
            having: new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterThan, Literal.FromInteger("1")));

        Assert.Null(sql);
    }

    /// <summary>
    /// An order a group row does not carry cannot be a group order: grouping by
    /// it as well would answer a different question (more groups), and ordering
    /// by an aggregate of the group is not a <c>GROUP BY</c> order at all. The
    /// reduction is reduced here instead, where the row order is knowable.
    /// </summary>
    [Fact]
    public void A_group_order_the_group_key_does_not_carry_is_declined()
    {
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: true,
            parameters: []);

        Assert.Null(sql);
    }

    [Fact]
    public void An_order_without_an_identity_column_is_declined_rather_than_left_unstable()
    {
        // No identity means no tie-break to append, so the order SQL could give
        // back is not total: a page boundary could fall between two rows the
        // next page re-orders (ADR-0098 §3).
        Assert.Null(PostgisPlanQueries.Order([new OrderTerm("city")], identityColumns: [], Schema, byteOrderText: true));

        Assert.Equal(
            ["\"city\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("city")], ["id"], Schema, byteOrderText: true));
    }

    [Fact]
    public void A_null_is_placed_explicitly_because_postgres_differs_per_direction()
    {
        Assert.Equal(
            ["\"population\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("population")], ["id"], Schema, byteOrderText: true));

        Assert.Equal(
            ["\"population\" DESC NULLS FIRST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("population", SortDirection.Descending)], ["id"], Schema, byteOrderText: true));
    }
}
