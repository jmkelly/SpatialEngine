using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// A pushed-down sort key over a <em>text</em> column, measured as SQL
/// (ADR-0098 §3, ADR-0121, ADR-0136).
///
/// <para>
/// The contract compares strings ordinally — by bytes, never by a locale
/// collation — and the reference executor honours that. Postgres does not say
/// so: a <c>text</c> column carries a collation, so an <c>ORDER BY</c> that
/// inherits it answers a different order for the same plan
/// (<c>en_US.utf8</c> puts <c>"a"</c> before <c>"A"</c> and sorts the
/// punctuated names to where their letters sort). The conformance suite proves
/// the answer; this pins the statement that answers it, in both of the two
/// shapes it can take: the term is there when the column does not already
/// compare by bytes and absent when it does, because a term on a sort that is
/// already the reference's only costs the planner an index. "Does this column
/// already compare by bytes" is the <em>column's</em> question rather than the
/// database's: its own collation where it declares one, the database's where
/// it declares none (ADR-0136).
/// </para>
///
/// <para>
/// Every case here is free of Npgsql and of a container: the shape is decided
/// by the collation of the column and the field's kind, and those are the two
/// inputs this file varies.</para>
/// </summary>
public sealed class PostgisTextOrderTests
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
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("city", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
    ]);

    /// <summary>A sort key over a text column, as the order terms the plan read carries.</summary>
    private static IReadOnlyList<string>? Order(
        PostgisTextOrder text,
        string field = "city",
        SortDirection direction = SortDirection.Ascending) =>
        PostgisPlanQueries.Order([new OrderTerm(field, direction)], ["id"], Schema, text);

    /// <summary>A dataset whose columns declare collations of their own, over a database (ADR-0136).</summary>
    private static PostgisTextOrder Declaring(string? database, params (string Column, string Collation)[] columns) =>
        new(database, columns.ToDictionary(column => column.Column, column => column.Collation, StringComparer.Ordinal));

    [Fact]
    public void A_text_sort_key_in_a_locale_collation_is_compared_by_bytes()
    {
        // en_US.utf8 is not the reference's order, so the statement has to say
        // which order it wants rather than inherit one.
        Assert.Equal(
            ["\"city\" COLLATE \"C\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(text: PostgisTextOrder.Locale));
    }

    [Fact]
    public void A_text_sort_key_in_a_byte_order_database_needs_no_term()
    {
        // The database already sorts these rows the way the reference does, so
        // `COLLATE "C"` would be a no-op that still stops the planner using the
        // column's own index.
        Assert.Equal(["\"city\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"], Order(text: PostgisTextOrder.ByteOrder));
    }

    [Fact]
    public void A_descending_text_sort_key_carries_the_term_with_its_null_placement()
    {
        Assert.Equal(
            ["\"city\" COLLATE \"C\" DESC NULLS FIRST", "\"id\" ASC NULLS LAST"],
            Order(text: PostgisTextOrder.Locale, direction: SortDirection.Descending));
    }

    [Fact]
    public void A_text_column_that_declares_a_locale_collation_states_the_order_on_a_byte_order_database()
    {
        // The database already compares by bytes, so every column that inherits
        // its collation needs no term — and this column does not inherit it.
        // `"label" text COLLATE "de-x-icu"` sorts by the column's own collation
        // whatever the database's, so the term is what makes this order the
        // reference's (ADR-0136).
        Assert.Equal(
            ["\"city\" COLLATE \"C\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("C", ("city", "de-x-icu"))));
    }

    [Fact]
    public void A_text_column_that_declares_the_byte_order_needs_no_term_on_a_locale_database()
    {
        // The other direction of the same rule, and the one that costs nothing
        // to get right: the column's own collation is already the reference's,
        // so a term over it is the no-op that stops the planner using the
        // column's index.
        Assert.Equal(
            ["\"city\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("en_US.utf8", ("city", "C"))));
    }

    [Fact]
    public void Each_column_is_ordered_by_the_collation_it_declares_itself()
    {
        // Two text columns over one dataset and the answer is per column rather
        // than per database: one declares the byte order and one does not, and
        // the plan's key and its tie-break are asked separately.
        Assert.Equal(
            ["\"city\" COLLATE \"C\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("C", ("city", "en-GB-x-icu"), ("population", "C")), "city"));
        Assert.Equal(
            ["\"population\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("C", ("city", "en-GB-x-icu"), ("population", "C")), "population"));
    }

    [Fact]
    public void A_column_that_declares_no_collation_is_the_databases_own()
    {
        // The map holds only the columns that declare one, so a column it does
        // not name is answered by the database — the case for every table this
        // store creates.
        Assert.Equal(
            ["\"city\" COLLATE \"C\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("en_US.utf8", ("other", "de-x-icu"))));
        Assert.Equal(
            ["\"city\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(Declaring("C", ("other", "de-x-icu"))));
    }

    [Fact]
    public void A_numeric_sort_key_never_carries_a_collation()
    {
        // COLLATE is a string operator: writing it over an integer column is a
        // statement Postgres refuses, so the term follows the field's kind and
        // not the database's collation.
        Assert.Equal(
            ["\"population\" ASC NULLS LAST", "\"id\" ASC NULLS LAST"],
            Order(text: PostgisTextOrder.Locale, "population"));
    }

    [Fact]
    public void A_text_identity_tie_break_is_compared_by_bytes_too()
    {
        // The tie-break is appended to the plan's own terms, so it inherits the
        // same collation question: a text primary key sorted by locale would
        // make the total order the store returns a different total order.
        Assert.Equal(
            ["\"id\" ASC NULLS LAST", "\"city\" COLLATE \"C\" ASC NULLS LAST"],
            PostgisPlanQueries.Order([new OrderTerm("id")], ["city"], Schema, text: PostgisTextOrder.Locale));
    }

    [Fact]
    public void A_group_order_over_a_text_key_is_compared_by_bytes()
    {
        // The grouped reduction is the other place a text sort key reaches SQL,
        // and the one the statistics surface serves: `GROUP BY` on a text
        // column, ordered by it. A group order under a locale collation returns
        // the groups in a sequence the reference never produced.
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("city")],
            Schema,
            text: PostgisTextOrder.Locale,
            parameters: []);

        Assert.NotNull(sql);
        Assert.Contains(" GROUP BY \"city\"", sql, StringComparison.Ordinal);
        Assert.Contains(
            " ORDER BY \"city\" COLLATE \"C\" ASC NULLS LAST, \"city\" COLLATE \"C\" ASC NULLS LAST",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_order_in_a_byte_order_database_needs_no_term()
    {
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("city")],
            Schema,
            text: PostgisTextOrder.ByteOrder,
            parameters: []);

        Assert.NotNull(sql);
        Assert.Contains(" ORDER BY \"city\" ASC NULLS LAST, \"city\" ASC NULLS LAST", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percentile_ranked_over_a_text_field_is_ranked_by_bytes()
    {
        // The `WITHIN GROUP` order is a sort key like any other: PERCENTILE_DISC
        // returns one of the ranked values, so the collation decides which row
        // the reduction answers with.
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.PercentileDiscrete, "city", "p50", 0.5)],
            [],
            Schema,
            text: PostgisTextOrder.Locale,
            parameters: []);

        Assert.NotNull(sql);
        Assert.Contains("WITHIN GROUP (ORDER BY \"city\" COLLATE \"C\" ASC)", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C", true)]
    [InlineData("c", true)]
    [InlineData("POSIX", true)]
    [InlineData("C.UTF-8", true)]
    [InlineData("en_US.utf8", false)]
    [InlineData("en-GB-x-icu", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_database_is_treated_as_comparing_by_bytes_only_when_it_names_a_byte_collation(
        string? collation, bool expected)
    {
        Assert.Equal(expected, PostgisTextCollation.IsByteOrder(collation));
    }

    [Fact]
    public void A_minimum_or_maximum_over_a_text_field_is_taken_by_bytes()
    {
        // The same question, asked as an extreme rather than as an order: an
        // ungrouped reduction pushes `MIN`/`MAX` over whatever field the
        // statistic named, and over a text column those are collation-ordered
        // too. Without the term a locale database answers the extreme of a
        // different row.
        var sql = PostgisPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Minimum, "city", "first"),
             new AggregateSpec(AggregateStatistic.Maximum, "city", "last"),
             new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [],
            Schema,
            text: PostgisTextOrder.Locale,
            parameters: []);

        Assert.NotNull(sql);
        Assert.Contains("MIN(\"city\" COLLATE \"C\")", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(\"city\" COLLATE \"C\")", sql, StringComparison.Ordinal);
        // A numeric statistic is untouched: COLLATE is a string operator.
        Assert.Contains("SUM(\"population\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(\"population\" COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_this_store_could_not_ask_is_treated_as_a_locale_collation()
    {
        // The term is the thing that makes the pushdown the contract's answer,
        // and a probe that failed has not learned that the answer was already
        // right — so the safe direction is the one that states the order.
        Assert.False(PostgisTextCollation.IsByteOrder(null));
        Assert.False(PostgisTextCollation.IsByteOrder("   "));
    }
}
