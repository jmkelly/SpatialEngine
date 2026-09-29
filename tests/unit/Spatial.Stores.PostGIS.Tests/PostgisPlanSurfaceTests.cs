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
            parameters);

        Assert.NotNull(sql);
        Assert.Contains(" GROUP BY \"city\"", sql);
        Assert.Contains(" ORDER BY \"city\" ASC NULLS LAST", sql);
        Assert.Contains("COUNT(*)", sql);
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
            parameters: []);

        Assert.Null(sql);
    }

    [Fact]
    public void An_order_without_an_identity_column_is_declined_rather_than_left_unstable()
    {
        // No identity means no tie-break to append, so the order SQL could give
        // back is not total: a page boundary could fall between two rows the
        // next page re-orders (ADR-0098 §3).
        Assert.Null(PostgisPlanQueries.Order([new OrderTerm("city")], identityColumns: []));

        Assert.Equal(
            ["\"city\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("city")], ["id"]));
    }

    [Fact]
    public void A_null_is_placed_explicitly_because_postgres_differs_per_direction()
    {
        Assert.Equal(
            ["\"population\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("population")], ["id"]));

        Assert.Equal(
            ["\"population\" DESC NULLS FIRST", "\"id\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("population", SortDirection.Descending)], ["id"]));
    }
}
