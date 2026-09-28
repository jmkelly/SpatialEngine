using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Core.Tests;

/// <summary>
/// The one table of comparable literal/column pairs the whole engine shares
/// (ADR-0097 §2). The in-memory store's evaluator and both SQL compilers each
/// answer "can this ever match?" in their own back end, and this is the
/// classification they all have to agree with — so it is pinned here, on its
/// own, rather than only through a provider that happens to use it.
/// </summary>
public sealed class PredicateCompatibilityTests
{
    private static readonly Literal Text = Literal.FromText("a");
    private static readonly Literal Whole = Literal.FromInteger("3");
    private static readonly Literal Fraction = Literal.FromNumber(2.5);
    private static readonly Literal Flag = Literal.FromBoolean(true);
    private static readonly Literal Moment = Literal.FromMilliseconds(1_700_000_000_000);
    private static readonly Literal Nothing = Literal.Null;

    [Theory]
    // A text column is a text column, and the only thing a LIKE takes.
    [InlineData(AttributeKind.String, "text", ComparisonOperator.Equals, true)]
    [InlineData(AttributeKind.String, "text", ComparisonOperator.Like, true)]
    // A numeric column takes any number, and a date-time because both are
    // instants on one axis.
    [InlineData(AttributeKind.Int64, "whole", ComparisonOperator.GreaterOrEqual, true)]
    [InlineData(AttributeKind.Int64, "fraction", ComparisonOperator.LessThan, true)]
    [InlineData(AttributeKind.Double, "whole", ComparisonOperator.Equals, true)]
    [InlineData(AttributeKind.Double, "fraction", ComparisonOperator.Equals, true)]
    [InlineData(AttributeKind.DateTimeOffset, "moment", ComparisonOperator.LessThan, true)]
    // A date-time is an instant, so it reads a number as the same epoch axis.
    [InlineData(AttributeKind.DateTimeOffset, "whole", ComparisonOperator.Equals, true)]
    // A boolean is only ever equal to a boolean.
    [InlineData(AttributeKind.Boolean, "flag", ComparisonOperator.Equals, true)]
    [InlineData(AttributeKind.Boolean, "flag", ComparisonOperator.LessThan, false)]
    // A guid is a guid-formatted string, and never a pattern.
    [InlineData(AttributeKind.Guid, "guid", ComparisonOperator.Equals, true)]
    [InlineData(AttributeKind.Guid, "guid", ComparisonOperator.Like, false)]
    [InlineData(AttributeKind.Guid, "text", ComparisonOperator.Equals, false)]
    // Nothing coerces, so a number is not text and text is not a number.
    [InlineData(AttributeKind.String, "whole", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.String, "flag", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.Int64, "text", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.Int64, "flag", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.Boolean, "text", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.DateTimeOffset, "text", ComparisonOperator.LessThan, false)]
    // A LIKE is a whole-value text test, so nothing else takes one.
    [InlineData(AttributeKind.Int64, "whole", ComparisonOperator.Like, false)]
    [InlineData(AttributeKind.DateTimeOffset, "moment", ComparisonOperator.Like, false)]
    // Geometry is not an attribute comparison at all.
    [InlineData(AttributeKind.Geometry, "text", ComparisonOperator.Equals, false)]
    [InlineData(AttributeKind.Geometry, "whole", ComparisonOperator.GreaterThan, false)]
    public void A_column_kind_and_a_literal_kind_decide_whether_a_comparison_can_match(
        AttributeKind kind, string literal, ComparisonOperator comparison, bool expected) =>
        Assert.Equal(expected, PredicateCompatibility.CanMatch(comparison, Value(literal), kind));

    [Fact]
    public void A_numeric_column_takes_every_numeric_literal()
    {
        foreach (var literal in new[] { Whole, Fraction, Moment })
        {
            Assert.True(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, literal, AttributeKind.Int64));
            Assert.True(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, literal, AttributeKind.Double));
        }
    }

    [Fact]
    public void Nothing_coerces_to_another_column_kind()
    {
        // The whole point of the table: a mismatched pair is "matches nothing",
        // not a conversion, so every back end can answer it the same way.
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Whole, AttributeKind.String));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Text, AttributeKind.Int64));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Text, AttributeKind.Boolean));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Flag, AttributeKind.Int64));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Moment, AttributeKind.String));
        // The two ends of the epoch axis do meet, which is why a numeric
        // column and a date-time column share one row of the table.
        Assert.True(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Moment, AttributeKind.DateTimeOffset));
        Assert.True(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Whole, AttributeKind.DateTimeOffset));
    }

    [Fact]
    public void A_null_literal_can_never_match()
    {
        // SQL's three-valued logic reads as "not matched", and the reference
        // evaluator gives the same answer, so the table says so once.
        foreach (var kind in new[] { AttributeKind.String, AttributeKind.Int64, AttributeKind.Double, AttributeKind.Boolean, AttributeKind.DateTimeOffset, AttributeKind.Guid })
        {
            Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Nothing, kind));
            Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.NotEquals, Nothing, kind));
        }
    }

    [Fact]
    public void A_guid_literal_must_be_guid_formatted()
    {
        var reference = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.True(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Literal.FromText(reference.ToString("D")), AttributeKind.Guid));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.Equals, Literal.FromText("not-a-guid"), AttributeKind.Guid));
    }

    [Fact]
    public void A_guid_column_compares_for_equality_and_order_but_not_by_pattern()
    {
        // The reference evaluator answers equality and inequality for a guid and
        // nothing else, because a guid has no ordering the engine promises.
        var reference = Literal.FromText("11111111-2222-3333-4444-555555555555");

        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.LessThan, reference, AttributeKind.Guid));
        Assert.False(PredicateCompatibility.CanMatch(ComparisonOperator.GreaterThan, reference, AttributeKind.Guid));
    }

    private static Literal Value(string kind) => kind switch
    {
        "text" => Text,
        "whole" => Whole,
        "fraction" => Fraction,
        "flag" => Flag,
        "moment" => Moment,
        "guid" => Literal.FromText("11111111-2222-3333-4444-555555555555"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
