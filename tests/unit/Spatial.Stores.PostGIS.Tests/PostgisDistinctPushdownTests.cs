using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The distinct set the PostGIS store compiles to SQL (ADR-0133 §6). A
/// <c>DISTINCT</c> is pushed for the plan whose order is <em>total over the
/// distinct rows</em> — every requested field named by a term of the plan's
/// order — and reduced over the read for every other plan, because SQL returns
/// distinct rows in no order the dialect states and the contract's order for
/// them is the plan's.
///
/// <para>
/// The shape is pinned here because the statement is the answer: a text column
/// is deduplicated and ordered under the byte-order collation (a locale
/// collation folds <c>"A"</c> onto <c>"a"</c>, which is not one value but two),
/// and the <c>ORDER BY</c> sits on an outer statement because Postgres takes an
/// <c>ORDER BY</c> over a <c>DISTINCT</c> only from its select list, where the
/// reference's null-placement key is not one of the requested fields.
/// </para>
/// </summary>
public sealed class PostgisDistinctPushdownTests
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
    public void A_distinct_set_is_pushed_when_the_plan_order_is_total_over_it()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Distinct(
            Dataset,
            ["city", "population"],
            [new OrderTerm("city"), new OrderTerm("population", SortDirection.Descending)],
            Schema,
            text: PostgisTextOrder.Locale,
            where: null,
            parameters);

        Assert.Equal(
            "SELECT * FROM (SELECT DISTINCT \"city\" COLLATE \"C\" AS \"city\", \"population\" FROM \"public\".\"places\") AS d"
            + " ORDER BY \"d\".\"city\" COLLATE \"C\" ASC NULLS LAST,"
            + " \"d\".\"population\" DESC NULLS FIRST",
            sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_database_that_already_compares_by_bytes_states_no_collation()
    {
        var sql = PostgisPlanQueries.Distinct(
            Dataset,
            ["city"],
            [new OrderTerm("city")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: null,
            parameters: new List<object?>());

        Assert.Equal(
            "SELECT * FROM (SELECT DISTINCT \"city\" FROM \"public\".\"places\") AS d"
            + " ORDER BY \"d\".\"city\" ASC NULLS LAST",
            sql);
    }

    [Fact]
    public void A_pushed_distinct_set_keeps_the_restriction_it_was_given()
    {
        var parameters = new List<object?> { 1000L };
        var sql = PostgisPlanQueries.Distinct(
            Dataset,
            ["population"],
            [new OrderTerm("population")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: "\"population\" > @p0",
            parameters);

        Assert.NotNull(sql);
        Assert.Contains("FROM \"public\".\"places\" WHERE \"population\" > @p0)", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void A_distinct_set_with_no_plan_order_is_reduced_here()
    {
        Assert.Null(PostgisPlanQueries.Distinct(
            Dataset,
            ["city"],
            order: [],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: null,
            parameters: new List<object?>()));
    }

    /// <summary>
    /// An order that names fewer of the requested fields (or a field that is not
    /// one of them) leaves the rows that tie on it in an order the dialect does
    /// not state, so the first-seen order is the reference's and is only
    /// knowable over the read.
    /// </summary>
    [Fact]
    public void A_distinct_set_the_plan_order_does_not_cover_is_reduced_here()
    {
        Assert.Null(PostgisPlanQueries.Distinct(
            Dataset,
            ["city", "population"],
            [new OrderTerm("city")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: null,
            parameters: new List<object?>()));

        Assert.Null(PostgisPlanQueries.Distinct(
            Dataset,
            ["city"],
            [new OrderTerm("population")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: null,
            parameters: new List<object?>()));
    }

    [Fact]
    public void A_distinct_set_of_a_geometry_field_is_reduced_here()
    {
        Assert.Null(PostgisPlanQueries.Distinct(
            Dataset,
            ["geom"],
            [new OrderTerm("geom")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            where: null,
            parameters: new List<object?>()));
    }
}
