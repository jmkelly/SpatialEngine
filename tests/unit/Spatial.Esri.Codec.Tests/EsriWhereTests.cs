using Spatial.Core.Features.Query;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The Esri <c>where</c> grammar as a front end over the engine's one
/// predicate vocabulary (ADR-0074 §7): the clause compiles to a
/// <see cref="Predicate"/>, the Esri-only parts fold to core values at parse
/// time, and the clause renders back to Esri text for the consuming provider.
/// Evaluation is a store's job, so nothing here matches a feature.
/// </summary>
public sealed class EsriWhereTests
{
    private static EsriWhere Parse(string text)
    {
        Assert.True(EsriWhere.TryParse(text, out var where, out var error), error);
        return where!;
    }

    private static Predicate PredicateOf(string text) => Parse(text).Predicate!;

    [Fact]
    public void A_comparison_compiles_to_the_core_vocabulary()
    {
        Assert.Equal(
            new Predicate.Compare(new FieldRef("name"), ComparisonOperator.Equals, Literal.FromText("Berlin")),
            PredicateOf("name = 'Berlin'"));
        Assert.Equal(
            new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("1000")),
            PredicateOf("population >= 1000"));
        Assert.Equal(
            new Predicate.Compare(new FieldRef("score"), ComparisonOperator.LessThan, Literal.FromNumber(2.5)),
            PredicateOf("score < 2.5"));
    }

    [Fact]
    public void A_whole_number_is_carried_verbatim()
    {
        // Verbatim text is what a store binds, so a value wider than a double
        // keeps every digit.
        Assert.Equal("12345678901234567890", ((Predicate.Compare)PredicateOf("population = 12345678901234567890")).Value.Text);
        Assert.Equal("-5", ((Predicate.Compare)PredicateOf("population >= -5")).Value.Text);
    }

    [Fact]
    public void Both_not_equals_spellings_compile_to_one_operator()
    {
        Assert.Equal(ComparisonOperator.NotEquals, ((Predicate.Compare)PredicateOf("name != 'Paris'")).Operator);
        Assert.Equal(ComparisonOperator.NotEquals, ((Predicate.Compare)PredicateOf("name <> 'Paris'")).Operator);
    }

    [Fact]
    public void Null_tests_and_membership_compile()
    {
        Assert.Equal(new Predicate.IsNull(new FieldRef("name"), false), PredicateOf("name IS NULL"));
        Assert.Equal(new Predicate.IsNull(new FieldRef("name"), true), PredicateOf("name IS NOT NULL"));
        var included = Assert.IsType<Predicate.IsIn>(PredicateOf("name IN ('a', 'b')"));
        Assert.Equal(new FieldRef("name"), included.Field);
        Assert.False(included.Negated);
        Assert.Equal([Literal.FromText("a"), Literal.FromText("b")], included.Values);

        var excluded = Assert.IsType<Predicate.IsIn>(PredicateOf("name NOT IN ('a')"));
        Assert.True(excluded.Negated);
        Assert.Equal([Literal.FromText("a")], excluded.Values);
    }

    [Fact]
    public void Logical_operators_nest_with_precedence()
    {
        // The tree keeps the grouping the text had: a disjunction of one
        // comparison and a nested conjunction, which renders with the
        // parentheses that preserve it.
        var disjunction = Assert.IsType<Predicate.Some>(PredicateOf("name = 'a' OR (population > 1 AND name != 'b')"));
        Assert.Equal(2, disjunction.Terms.Count);
        Assert.IsType<Predicate.Compare>(disjunction.Terms[0]);
        var conjunction = Assert.IsType<Predicate.Every>(disjunction.Terms[1]);
        Assert.Equal(2, conjunction.Terms.Count);
        Assert.Equal("(name = 'a' OR (population > 1 AND name <> 'b'))", EsriWhereText.Render(disjunction));
    }

    [Fact]
    public void A_literal_to_literal_comparison_folds_to_a_constant()
    {
        // The Esri match-all / match-none idioms reference no column, so they
        // must not require a schema lookup (ArcGIS REST JS sends where=1=1 by
        // default).
        Assert.Equal(new Predicate.Constant(true), PredicateOf("1=1"));
        Assert.Equal(new Predicate.Constant(false), PredicateOf("1=0"));
        Assert.Equal(new Predicate.Constant(true), PredicateOf("1 <> 2"));
        Assert.Equal(new Predicate.Constant(false), PredicateOf("TRUE = 1"));
        Assert.Equal(new Predicate.Constant(true), PredicateOf("'a' LIKE 'a%'"));
        Assert.Equal(new Predicate.Constant(false), PredicateOf("'a' = 'b'"));
    }

    [Fact]
    public void A_timestamp_literal_folds_to_a_date_time()
    {
        var compare = (Predicate.Compare)PredicateOf("when = TIMESTAMP '2023-11-14T22:13:20Z'");

        Assert.Equal(LiteralKind.DateTime, compare.Value.Kind);
        Assert.Equal(
            DateTimeOffset.Parse("2023-11-14T22:13:20Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
            (long)compare.Value.Number);
    }

    [Fact]
    public void Current_timestamp_folds_to_the_moment_it_was_parsed()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var compare = (Predicate.Compare)PredicateOf("when < CURRENT_TIMESTAMP - INTERVAL 1 DAY");
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal(LiteralKind.DateTime, compare.Value.Kind);
        Assert.InRange((long)compare.Value.Number, before - 86_400_000 - 1000, after - 86_400_000 + 1000);
    }

    [Fact]
    public void A_quoted_identifier_is_lexed_as_a_field_reference()
    {
        Assert.Equal(new FieldRef("name"), ((Predicate.Compare)PredicateOf("\"name\" = 'Berlin'")).Field);
    }

    [Fact]
    public void Referenced_fields_lists_comparison_and_null_test_fields_once()
    {
        Assert.Equal(
            ["population", "name"],
            Parse("population > 1 AND name IS NOT NULL AND population < 9").ReferencedFields);
        Assert.Empty(Parse("1=1").ReferencedFields);
    }

    [Fact]
    public void To_where_round_trips_the_supported_grammar()
    {
        var where = Parse("name = 'O''Brien' AND population > 10 OR name IS NULL");

        Assert.Equal("((name = 'O''Brien' AND population > 10) OR name IS NULL)", where.ToWhere());
        Assert.True(EsriWhere.TryParse(where.ToWhere(), out _, out _));
    }

    [Fact]
    public void To_where_renders_every_operator_and_literal_shape()
    {
        Assert.Equal("name <= 'b'", Parse("name <= 'b'").ToWhere());
        Assert.Equal("name <> 'b'", Parse("name <> 'b'").ToWhere());
        Assert.Equal("population >= -5", Parse("population >= -5").ToWhere());
        Assert.Equal("score = 1.5", Parse("score = 1.5").ToWhere());
        Assert.Equal("flag = TRUE", Parse("flag = TRUE").ToWhere());
        Assert.Equal("name = NULL", Parse("name = NULL").ToWhere());
        Assert.Equal("name LIKE 'a%'", Parse("name LIKE 'a%'").ToWhere());
        Assert.Equal("name IS NOT NULL", Parse("name IS NOT NULL").ToWhere());
        Assert.Equal("name IN ('a', 'b')", Parse("name IN ('a', 'b')").ToWhere());
        Assert.Equal("1 = 1", Parse("1=1").ToWhere());
        Assert.Equal("1 = 0", Parse("1=0").ToWhere());
    }

    [Fact]
    public void A_timestamp_round_trips_through_where_rendering()
    {
        var where = Parse("when >= TIMESTAMP '2024-01-01 00:00:00'");

        Assert.True(EsriWhere.TryParse(where.ToWhere(), out var reparsed, out var error), error);
        Assert.Equal(where.Predicate, reparsed!.Predicate);
    }

    [Theory]
    [InlineData("1=1; DROP TABLE features")]
    [InlineData("name = ")]
    [InlineData("name = 'unterminated")]
    [InlineData("LOWER(name) = 'x'")]
    [InlineData("population = 1.2.3")]
    [InlineData("name = AND")]
    [InlineData("name IS 1")]
    [InlineData("(name = 'x'")]
    [InlineData("name @ 1")]
    [InlineData("name IN ('a'")]
    [InlineData("name IN 'a')")]
    [InlineData("name NOT a")]
    [InlineData("when = TIMESTAMP 'not-a-date'")]
    [InlineData("when = TIMESTAMP 123")]
    [InlineData("when = CURRENT_TIMESTAMP INTERVAL 1 DAY")]
    [InlineData("when > CURRENT_TIMESTAMP - INTERVAL 1 FORTNIGHT")]
    [InlineData("when > CURRENT_TIMESTAMP + TIMESTAMP '2024-01-01 00:00:00'")]
    public void Unsupported_constructs_are_rejected(string text)
    {
        Assert.False(EsriWhere.TryParse(text, out _, out _));
    }

    [Fact]
    public void And_conjoins_two_clauses()
    {
        var both = Parse("population > 1").And(Parse("name = 'Berlin'"));

        Assert.Equal("(population > 1 AND name = 'Berlin')", both.ToWhere());
        Assert.Equal(["population", "name"], both.ReferencedFields);
    }

    [Fact]
    public void And_flattens_adjacent_conjunctions()
    {
        var both = Parse("population > 1 AND population < 9")
            .And(Parse("name = 'Berlin' AND population > 2"));

        Assert.Equal(
            Parse("population > 1 AND population < 9 AND name = 'Berlin' AND population > 2").ToWhere(),
            both.ToWhere());
    }

    [Fact]
    public void And_with_an_absent_clause_is_the_other_clause()
    {
        var present = Parse("name = 'Berlin'");

        Assert.Equal(present, present.And(EsriWhere.None));
        Assert.Equal(present, EsriWhere.None.And(present));
    }
}
