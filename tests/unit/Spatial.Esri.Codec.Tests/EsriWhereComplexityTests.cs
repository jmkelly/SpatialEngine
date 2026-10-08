using Spatial.Core.Features.Query;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The complexity hotspots of the where grammar: the literal-to-literal truth
/// table, every render arm, the operator table read as text, and the parser
/// failure paths. Each case pins real behavior, not the split structure.
/// </summary>
public sealed class EsriWhereComplexityTests
{
    private static EsriWhere Parse(string text)
    {
        Assert.True(EsriWhere.TryParse(text, out var where, out var error), error);
        return where!;
    }

    private static Predicate.Constant ConstantOf(string text) =>
        Assert.IsType<Predicate.Constant>(Parse(text).Predicate);

    [Theory]
    [InlineData("TRUE = TRUE", true)]
    [InlineData("TRUE = FALSE", false)]
    [InlineData("TRUE <> FALSE", true)]
    [InlineData("FALSE <> FALSE", false)]
    [InlineData("TRUE < FALSE", false)]
    [InlineData("1 = 1", true)]
    [InlineData("1 = 0", false)]
    [InlineData("2 > 1", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("2.5 >= 2.5", true)]
    [InlineData("'a' = 'a'", true)]
    [InlineData("'a' <> 'b'", true)]
    [InlineData("'b' < 'a'", false)]
    [InlineData("'abc' LIKE 'a%'", true)]
    [InlineData("'abc' LIKE 'z%'", false)]
    [InlineData("NULL = NULL", false)]
    [InlineData("1 = NULL", false)]
    [InlineData("1 LIKE 2", false)]
    public void A_literal_comparison_folds_to_its_truth(string text, bool expected)
    {
        Assert.Equal(expected, ConstantOf(text).Value);
    }

    [Fact]
    public void A_timestamp_comparison_folds_numerically()
    {
        Assert.True(ConstantOf("TIMESTAMP '2020-01-02T00:00:00Z' > TIMESTAMP '2020-01-01T00:00:00Z'").Value);
        Assert.False(ConstantOf("TIMESTAMP '2020-01-01T00:00:00Z' > TIMESTAMP '2020-01-02T00:00:00Z'").Value);
    }

    [Theory]
    [InlineData("population = 1", "population = 1")]
    [InlineData("1 = 1", "1 = 1")]
    [InlineData("1 = 0", "1 = 0")]
    [InlineData("name IS NULL", "name IS NULL")]
    [InlineData("name IS NOT NULL", "name IS NOT NULL")]
    [InlineData("name IN ('a', 'b')", "name IN ('a', 'b')")]
    [InlineData("name NOT IN ('a')", "name NOT IN ('a')")]
    [InlineData("a = 1 AND b = 2", "(a = 1 AND b = 2)")]
    [InlineData("a = 1 OR b = 2", "(a = 1 OR b = 2)")]
    public void Every_predicate_node_renders(string text, string expected)
    {
        Assert.Equal(expected, Parse(text).ToWhere());
    }

    [Theory]
    [InlineData("'O''Brien'", "'O''Brien'")]
    [InlineData("42", "42")]
    [InlineData("2.5", "2.5")]
    [InlineData("TRUE", "TRUE")]
    [InlineData("FALSE", "FALSE")]
    [InlineData("NULL", "NULL")]
    public void Every_literal_kind_renders(string literal, string expected)
    {
        Assert.Equal($"name = {expected}", Parse($"name = {literal}").ToWhere());
    }

    [Fact]
    public void A_datetime_literal_renders_as_a_timestamp()
    {
        var rendered = Parse("when = TIMESTAMP '2020-01-02T00:00:00Z'").ToWhere();

        Assert.StartsWith("when = TIMESTAMP '2020-01-02", rendered);
    }

    [Theory]
    [InlineData(ComparisonOperator.Equals, "=")]
    [InlineData(ComparisonOperator.NotEquals, "<>")]
    [InlineData(ComparisonOperator.LessThan, "<")]
    [InlineData(ComparisonOperator.LessOrEqual, "<=")]
    [InlineData(ComparisonOperator.GreaterThan, ">")]
    [InlineData(ComparisonOperator.GreaterOrEqual, ">=")]
    [InlineData(ComparisonOperator.Like, "LIKE")]
    public void Every_operator_spells(ComparisonOperator comparison, string expected)
    {
        Assert.Equal(expected, EsriWhereText.OperatorText(comparison));
    }

    [Fact]
    public void An_unknown_operator_is_a_typed_failure()
    {
        Assert.Throws<EsriInteropException>(() => EsriWhereText.OperatorText((ComparisonOperator)99));
    }

    [Fact]
    public void Only_the_spelled_operators_are_statable()
    {
        Assert.True(EsriWhereText.Spells(ComparisonOperator.Like));
        Assert.False(EsriWhereText.Spells(ComparisonOperator.LikeFolded));
        Assert.Null(EsriWhereText.Statable(new Predicate.Compare(new FieldRef("n"), ComparisonOperator.LikeFolded, Literal.FromText("a%"))));
    }

    [Theory]
    [InlineData("name ? 'x'")]
    [InlineData("name = ")]
    [InlineData("(name = 'a'")]
    [InlineData("= 1")]
    [InlineData("name IS")]
    [InlineData("name IS NOT")]
    [InlineData("name IN")]
    [InlineData("name IN ('a'")]
    [InlineData("name IN ()")]
    [InlineData("TIMESTAMP 'not-a-date' = TIMESTAMP '2020-01-01T00:00:00Z'")]
    [InlineData("TIMESTAMP 42 = 1")]
    [InlineData("CURRENT_TIMESTAMP + INTERVAL")]
    [InlineData("CURRENT_TIMESTAMP + INTERVAL 1 FURLONG")]
    [InlineData("CURRENT_TIMESTAMP - INTERVAL 1 DAY EXTRA")]
    public void A_malformed_clause_reports_its_position(string text)
    {
        Assert.False(EsriWhere.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_current_timestamp_with_interval_parses()
    {
        var predicate = Parse("when > CURRENT_TIMESTAMP - INTERVAL 1 DAY").Predicate;

        Assert.IsType<Predicate.Compare>(predicate);
    }

    [Fact]
    public void Trailing_text_after_a_clause_is_a_failure()
    {
        Assert.False(EsriWhere.TryParse("name = 'a' EXTRA", out _, out var error));
        Assert.Contains("EXTRA", error);
    }
}
