using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// Which plans the store addresses as a <em>page</em> in SQL and which it
/// finishes over the whole read (ADR-0116 §1). The distinction is the memory
/// claim: a plan read with a <c>LIMIT</c> never holds more than a page, and a
/// plan finished over a whole read does.
///
/// <para>
/// The rule is not "push what you can". A page is only addressable when the
/// plan's order is an <c>ORDER BY</c> this table can make total — the identity
/// tie-break included — because a row's place in a page is its place in an
/// order, and an <c>OFFSET</c> into an order the next statement cannot
/// reproduce is not a position at all. So a plan that asks for no order is read
/// whole and paged in memory, however large the layer; a plan that asks for an
/// order this table cannot express is too; and a plan that asks for no
/// <em>restriction</em> is read whole only for the same reason — otherwise a
/// large layer is read with a <c>LIMIT</c> like any other page.
/// </para>
/// </summary>
public sealed class PostgisPlanPagingTests
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

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("city", AttributeKind.String, false),
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("geom", AttributeKind.Geometry, false)]);

    private static readonly IReadOnlyList<string> TotalOrder = ["\"id\" ASC NULLS LAST"];

    [Fact]
    public void An_unrestricted_but_ordered_plan_is_pushed_as_a_page()
    {
        // The plan that was the shallow edge: nothing restricts it, so a naive
        // reader fetches the table and pages on the way in. The identity order
        // makes the page addressable, so it is a LIMIT.
        Assert.True(PostgisPlanReader.Pushed(where: null, new FeatureQuery(Order: [new OrderTerm("id")]), TotalOrder));
    }

    [Fact]
    public void An_unrestricted_ordered_plan_reads_a_cap_and_a_start_and_names_no_column_of_rows()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Read(
            Dataset,
            PostgisPlanQueries.Columns(Schema, null),
            where: null,
            order: TotalOrder,
            paging: new PostgisPlanQueries.Paging(20, 40),
            parameters);

        Assert.Equal(
            "SELECT \"id\", \"city\", \"population\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\""
            + " ORDER BY \"id\" ASC NULLS LAST LIMIT @p0 OFFSET @p1",
            sql);
        Assert.Equal([20, 40], parameters);
    }

    [Fact]
    public void An_unordered_plan_is_finished_over_the_whole_read()
    {
        // No order: the plan's order is the scan order, and an OFFSET into a
        // scan order is not a position the next statement can reproduce.
        Assert.False(PostgisPlanReader.Pushed(where: null, FeatureQuery.All, order: null));
    }

    [Fact]
    public void A_restricted_plan_is_pushed_whatever_the_order()
    {
        // A restriction the dialect expressed is a statement about which rows
        // exist, so the page can be addressed by the cap alone: the rows it
        // skips are rows the plan did not match anyway.
        Assert.True(PostgisPlanReader.Pushed("\"population\" > @p0", FeatureQuery.All, order: null));
    }

    [Fact]
    public void A_plan_whose_order_the_table_cannot_make_total_is_finished_over_the_whole_read()
    {
        Assert.False(
            PostgisPlanReader.Pushed(
                "\"population\" > @p0", new FeatureQuery(Order: [new OrderTerm("id")]), order: null));
    }

    [Fact]
    public void A_read_with_no_cap_is_the_whole_table_and_says_so()
    {
        var parameters = new List<object?>();
        var sql = PostgisPlanQueries.Read(
            Dataset,
            PostgisPlanQueries.Columns(Schema, null),
            where: null,
            order: TotalOrder,
            paging: new PostgisPlanQueries.Paging(null, 0),
            parameters);

        Assert.Equal(
            "SELECT \"id\", \"city\", \"population\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\""
            + " ORDER BY \"id\" ASC NULLS LAST",
            sql);
        Assert.Empty(parameters);
    }

    /// <summary>
    /// The two halves of the same rule on a dataset that declares no identity
    /// column: its features are named by the ordinal of the read, so the store
    /// may not push a restriction into SQL (a <c>WHERE</c> renumbers every
    /// feature after the first match, ADR-0097 §1) and may not address a page
    /// (no identity tie-break, so the plan's order is not one an
    /// <c>OFFSET</c> can name, ADR-0116 §1). Both declines mean the same thing
    /// to a caller: the plan is answered over the whole read, which is a
    /// correct answer and a table-sized read.
    ///
    /// <para>
    /// Pinned here because the cost is invisible from the answer. A layer
    /// created by <c>CreateAsync</c> from a schema with no integer identity
    /// field has no primary key, and every plan against it reads the whole
    /// table: the query baseline measured exactly that (46–48 MB per capped
    /// call against 31 MB for the full scan, and neither figure moves with the
    /// page size — SpatialEngine-d20). Nothing in the store decides this per
    /// call; it is decided by the dataset's description, here.
    /// </para>
    /// </summary>
    [Fact]
    public void A_dataset_with_no_identity_column_declines_both_the_restriction_and_the_page()
    {
        // The same places schema, described without a primary key — what the
        // store's own create leaves when the sampled schema has no integer
        // identity field (PostgisQueries.CreateTable emits no PRIMARY KEY).
        var keyless = new DatasetDescription(
            "public.places",
            "public",
            "places",
            "geom",
            4326,
            "POINT",
            3,
            [],
            Schema);
        var plan = new FeatureQuery(
            BoundingBox: new BoundingBox(0, 0, 10, 10),
            Where: new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("200")),
            Order: [new OrderTerm("population", SortDirection.Descending)],
            Limit: 25);
        var parameters = new List<object?>();

        Assert.Null(PostgisPlanQueries.Predicate(Dataset, keyless, plan, PostgisTextOrder.Locale, parameters));
        Assert.NotNull(plan.Order);
        Assert.Null(PostgisPlanQueries.Order(plan.Order, keyless.IdColumns, keyless.Schema, PostgisTextOrder.Locale));
        Assert.False(PostgisPlanReader.Pushed(where: null, plan, order: null));

        // Nothing was bound for a fragment that was never written: the read
        // that follows is the whole table, with no parameters on it.
        Assert.Empty(parameters);
    }
}
