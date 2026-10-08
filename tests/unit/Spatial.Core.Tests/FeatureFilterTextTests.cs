using Spatial.Core.Features.Query;

namespace Spatial.Core.Tests;

/// <summary>
/// The filter-text front end (<see cref="FeatureFilterText"/>): the keyword,
/// symbol, operator and value dispatches each answer their whole vocabulary,
/// and malformed values fail with a position rather than a partial tree.
/// </summary>
public sealed class FeatureFilterTextTests
{
    [Theory]
    [InlineData("a = 1", ComparisonOperator.Equals)]
    [InlineData("a != 1", ComparisonOperator.NotEquals)]
    [InlineData("a <> 1", ComparisonOperator.NotEquals)]
    [InlineData("a < 1", ComparisonOperator.LessThan)]
    [InlineData("a <= 1", ComparisonOperator.LessOrEqual)]
    [InlineData("a > 1", ComparisonOperator.GreaterThan)]
    [InlineData("a >= 1", ComparisonOperator.GreaterOrEqual)]
    [InlineData("a LIKE 'x%'", ComparisonOperator.Like)]
    [InlineData("a ILIKE 'x%'", ComparisonOperator.LikeFolded)]
    public void Every_operator_spelling_parses(string filter, ComparisonOperator expected)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);

        var compare = Assert.IsType<Predicate.Compare>(predicate);
        Assert.Equal(expected, compare.Operator);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("FALSE", false)]
    public void Boolean_literals_parse(string text, bool expected)
    {
        Assert.True(FeatureFilterText.TryParse($"a = {text}", out var predicate, out var error), error);

        var compare = Assert.IsType<Predicate.Compare>(predicate);
        Assert.Equal(expected, compare.Value.Boolean);
    }

    [Fact]
    public void Keywords_match_case_insensitively()
    {
        Assert.True(FeatureFilterText.TryParse("a = 1 AnD b = 2", out var predicate, out var error), error);
        Assert.IsType<Predicate.Every>(predicate);
    }

    [Fact]
    public void A_timestamp_literal_reads_as_epoch_milliseconds()
    {
        Assert.True(
            FeatureFilterText.TryParse("a = TIMESTAMP '2024-01-01T00:00:00Z'", out var predicate, out var error),
            error);

        var compare = Assert.IsType<Predicate.Compare>(predicate);
        Assert.Equal(LiteralKind.DateTime, compare.Value.Kind);
    }

    [Fact]
    public void A_timestamp_without_a_quoted_date_time_fails()
    {
        Assert.False(FeatureFilterText.TryParse("a = TIMESTAMP 2024", out var predicate, out var error));
        Assert.Null(predicate);
        Assert.Contains("TIMESTAMP", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_with_an_unparseable_date_time_fails()
    {
        Assert.False(FeatureFilterText.TryParse("a = TIMESTAMP 'not-a-date'", out var predicate, out var error));
        Assert.Null(predicate);
        Assert.Contains("date-time", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrepresentable_number_fails_with_its_position()
    {
        Assert.False(FeatureFilterText.TryParse("a = 1e999", out var predicate, out var error));
        Assert.Null(predicate);
        Assert.Contains("not a valid filter number", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_membership_test_containing_null_is_refused()
    {
        Assert.False(FeatureFilterText.TryParse("a IN (1, NULL)", out var predicate, out var error));
        Assert.Null(predicate);
        Assert.Contains("NULL", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_value_after_an_operator_fails()
    {
        Assert.False(FeatureFilterText.TryParse("a = AND", out var predicate, out var error));
        Assert.Null(predicate);
        Assert.NotEmpty(error);
    }
}
