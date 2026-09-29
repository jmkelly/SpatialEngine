using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// A string comparison in a pushed-down <c>WHERE</c>, measured as SQL
/// (ADR-0098 §3, ADR-0121, ADR-0123).
///
/// <para>
/// The contract compares strings ordinally — by bytes, never by a locale
/// collation — and the reference executor honours that, so <c>code &lt;
/// 'delta'</c> selects the rows that are below <c>delta</c> <em>as bytes</em>.
/// Postgres says nothing about that: a <c>text</c> column carries the
/// database's default collation, and the stock container's <c>en_US.utf8</c>
/// orders <c>alpha, beta, _bravo, delta, Delta, epsilon, gamma</c> where the
/// contract's order is <c>Delta, _bravo, alpha, beta, delta, epsilon,
/// gamma</c>. A <c>WHERE</c> that inherits it selects a different row set for
/// the same plan — the same defect ADR-0121 fixed for the sort key, in the
/// statement that decides <em>which</em> rows a query sees at all.
///
/// <para>
/// Every case here is free of Npgsql and of a container: the shape is decided
/// by the database's collation and the field's kind, and those are the two
/// inputs this file varies. The value the store reads is proved by
/// <c>PostgisPredicateConformanceTests</c>, which runs the shared suite over a
/// table whose restriction really is pushed down.
/// </para>
/// </summary>
public sealed class PostgisPredicateCollationTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("code", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
    ]);

    private static string Where(string filter, bool byteOrderText)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return PostgisPredicateSql.Where(predicate!, Schema, byteOrderText, []);
    }

    [Theory]
    [InlineData("code < 'delta'", "\"code\" COLLATE \"C\" < @p0")]
    [InlineData("code <= 'delta'", "\"code\" COLLATE \"C\" <= @p0")]
    [InlineData("code = 'delta'", "\"code\" COLLATE \"C\" = @p0")]
    [InlineData("code != 'delta'", "\"code\" COLLATE \"C\" != @p0")]
    [InlineData("code > 'delta'", "\"code\" COLLATE \"C\" > @p0")]
    [InlineData("code >= 'delta'", "\"code\" COLLATE \"C\" >= @p0")]
    public void A_text_comparison_in_a_locale_collation_states_the_byte_order(string filter, string expected)
    {
        // Every ordering operator is the same question — "is this row's code
        // below the one in the filter" — and the collation decides which rows
        // that is, so every one of them states the byte order.
        Assert.Equal(expected, Where(filter, byteOrderText: false));
    }

    [Fact]
    public void A_text_comparison_in_a_byte_order_database_needs_no_term()
    {
        // The database already compares these rows the way the reference does,
        // so `COLLATE "C"` is a no-op that still stops the planner using the
        // column's own index (ADR-0121).
        Assert.Equal("\"code\" < @p0", Where("code < 'delta'", byteOrderText: true));
    }

    [Fact]
    public void A_text_membership_states_the_byte_order_too()
    {
        // `IN` is a list of equalities, and a case-insensitive collation folds
        // `Delta` onto `delta` — so the list matches a row the reference says
        // it does not.
        Assert.Equal("\"code\" COLLATE \"C\" IN (@p0, @p1)", Where("code IN ('a', 'b')", byteOrderText: false));
        Assert.Equal("\"code\" COLLATE \"C\" NOT IN (@p0)", Where("code NOT IN ('a')", byteOrderText: false));
    }

    [Fact]
    public void A_text_pattern_states_the_byte_order_too()
    {
        // A `LIKE` is a whole-value text test, and the reference evaluator
        // matches it as an ordinal pattern: under a case-folding collation
        // (`en_US-x-icu`, and SQL Server's own default) it would match a set
        // the reference never produced.
        Assert.Equal("\"code\" COLLATE \"C\" LIKE @p0", Where("code LIKE 'a%'", byteOrderText: false));
        Assert.Equal("\"code\" LIKE @p0", Where("code LIKE 'a%'", byteOrderText: true));
    }

    [Theory]
    [InlineData("population > 1000", "\"population\" > @p0")]
    [InlineData("active = TRUE", "\"active\" = @p0")]
    [InlineData("seen > 1000", "\"seen\" > @p0")]
    [InlineData("code IS NULL", "\"code\" IS NULL")]
    [InlineData("code IS NOT NULL", "\"code\" IS NOT NULL")]
    public void A_comparison_of_anything_but_text_never_carries_a_collation(string filter, string expected)
    {
        // `COLLATE` is a string operator: writing it over a number, a date or
        // a boolean is a statement Postgres refuses, so the term follows the
        // field's kind and not the database's collation. A null test is not a
        // comparison of values and has no collation to choose.
        Assert.Equal(expected, Where(filter, byteOrderText: false));
    }

    [Fact]
    public void A_grouped_filter_states_the_byte_order_on_the_text_half_only()
    {
        Assert.Equal(
            "\"population\" > @p0 AND \"code\" COLLATE \"C\" < @p1",
            Where("population > 1000 AND code < 'delta'", byteOrderText: false));
    }

    [Fact]
    public void A_comparison_the_column_cannot_answer_still_matches_nothing()
    {
        // The `FALSE` a non-comparable literal compiles to is the whole
        // fragment, so there is no column left to collate — and the answer is
        // still the reference's.
        Assert.Equal("FALSE", Where("code = 5", byteOrderText: false));
    }

    [Fact]
    public void A_plan_that_compares_text_is_asked_whether_it_needs_the_collation()
    {
        // The catalog read that decides the term is a round trip, and a plan
        // over a number and a bounding box cannot use its answer — so the
        // store asks this question first, and only then reads the collation.
        Assert.True(Compares("code < 'delta'"));
        Assert.True(Compares("code IN ('a')"));
        Assert.True(Compares("population > 1 OR code = 'a'"));
        // A null test is not a comparison of values: no collation changes which
        // rows are null, so asking for one would be a round trip for nothing.
        Assert.False(Compares("code IS NULL"));
        Assert.False(Compares("population > 1000"));
        Assert.False(Compares("population > 1000 AND active = TRUE"));
        Assert.False(Compares("active = TRUE OR seen > 1000"));
    }

    private static bool Compares(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return PostgisPredicateSql.ComparesText(predicate!, Schema);
    }
}
