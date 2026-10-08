using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The small querying seams: the row projection (identity shares the row's
/// own feature, a narrowed schema rebuilds it, an unknown field fails once),
/// the value ordering across every kind, the exact whole-number comparisons
/// past 2^53, and the empty reduction's one group of nulls.
/// </summary>
public sealed class QueryingSeamTests
{
    private static readonly FeatureSchema Read = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
    ]);

    [Fact]
    public void An_identical_projection_shares_the_row_feature()
    {
        var projection = new FeatureRowProjection(Read, Read);
        var feature = new Feature(new FeatureId("row"), Read, [AttributeValue.FromString("x"), AttributeValue.FromInt64(1)]);

        Assert.Same(feature, projection.Apply(feature));
    }

    [Fact]
    public void A_narrowed_projection_rebuilds_the_feature()
    {
        var result = new FeatureSchema([new FieldDefinition("population", AttributeKind.Int64, nullable: true)]);
        var projection = new FeatureRowProjection(Read, result);
        var feature = new Feature(new FeatureId("row"), Read, [AttributeValue.FromString("x"), AttributeValue.FromInt64(7)]);

        var projected = projection.Apply(feature);

        Assert.Equal(result, projected.Schema);
        Assert.Equal(AttributeValue.FromInt64(7), projected[0]);
    }

    [Fact]
    public void A_result_field_the_read_does_not_carry_fails_once()
    {
        var result = new FeatureSchema([new FieldDefinition("missing", AttributeKind.String, nullable: true)]);
        var failure = Assert.Throws<ArgumentException>(() => new FeatureRowProjection(Read, result));
        Assert.Contains("'missing'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_sort_last_and_values_order_within_their_kind()
    {
        Assert.Equal(1, AttributeValueComparer.Instance.Compare(AttributeValue.Null, AttributeValue.FromInt64(1)));
        Assert.Equal(-1, AttributeValueComparer.Instance.Compare(AttributeValue.FromInt64(1), AttributeValue.Null));
        Assert.Equal(0, AttributeValueComparer.Instance.Compare(AttributeValue.Null, AttributeValue.Null));
        Assert.True(AttributeValueComparer.Instance.Compare(AttributeValue.FromString("a"), AttributeValue.FromString("b")) < 0);
        Assert.True(AttributeValueComparer.Instance.Compare(AttributeValue.FromDouble(1), AttributeValue.FromDouble(2)) < 0);
        Assert.True(AttributeValueComparer.Instance.Compare(AttributeValue.FromBoolean(false), AttributeValue.FromBoolean(true)) < 0);
    }

    [Fact]
    public void Date_times_guids_and_envelopes_order()
    {
        var early = AttributeValue.FromDateTimeOffset(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var late = AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(AttributeValueComparer.Instance.Compare(early, late) < 0);

        var first = AttributeValue.FromGuid(new Guid("11111111-2222-3333-4444-555555555555"));
        var second = AttributeValue.FromGuid(new Guid("22222222-2222-3333-4444-555555555555"));
        Assert.True(AttributeValueComparer.Instance.Compare(first, second) < 0);

        var small = AttributeValue.FromEnvelope(new Envelope(0, 0, 1, 1));
        var large = AttributeValue.FromEnvelope(new Envelope(0, 0, 2, 2));
        Assert.True(AttributeValueComparer.Instance.Compare(small, large) < 0);
        Assert.Equal(0, AttributeValueComparer.Instance.Compare(small, small));
    }

    [Fact]
    public void Whole_numbers_compare_exactly_past_two_to_the_53()
    {
        var wide = (1L << 53) + 1;
        var schema = new FeatureSchema([new FieldDefinition("population", AttributeKind.Int64)]);
        var feature = new Feature(new FeatureId("row"), schema, [AttributeValue.FromInt64(wide)]);

        Assert.True(ReferencePredicate.Matches(
            new Predicate.Compare(new FieldRef("population"), ComparisonOperator.Equals, Literal.FromInteger(wide.ToString(CultureInfo.InvariantCulture))),
            feature));
        Assert.False(ReferencePredicate.Matches(
            new Predicate.Compare(new FieldRef("population"), ComparisonOperator.LessThan, Literal.FromInteger(wide.ToString(CultureInfo.InvariantCulture))),
            feature));
        Assert.True(ReferencePredicate.Matches(
            new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("3")),
            feature));
        Assert.False(ReferencePredicate.Matches(
            new Predicate.Compare(new FieldRef("population"), ComparisonOperator.Equals, Literal.FromNumber(3.5)),
            feature));
    }

    [Theory]
    [InlineData(ComparisonOperator.LessThan, "8", true)]
    [InlineData(ComparisonOperator.LessThan, "7", false)]
    [InlineData(ComparisonOperator.LessThan, "6", false)]
    [InlineData(ComparisonOperator.LessOrEqual, "8", true)]
    [InlineData(ComparisonOperator.LessOrEqual, "7", true)]
    [InlineData(ComparisonOperator.LessOrEqual, "6", false)]
    [InlineData(ComparisonOperator.GreaterThan, "6", true)]
    [InlineData(ComparisonOperator.GreaterThan, "7", false)]
    [InlineData(ComparisonOperator.GreaterOrEqual, "7", true)]
    [InlineData(ComparisonOperator.GreaterOrEqual, "8", false)]
    public void Whole_numbers_order_on_both_sides(ComparisonOperator comparison, string bound, bool expected)
    {
        // The population is 7: the lower pair and the upper pair both answer,
        // exactly past 2^53 as below it.
        var schema = new FeatureSchema([new FieldDefinition("population", AttributeKind.Int64)]);
        var feature = new Feature(new FeatureId("row"), schema, [AttributeValue.FromInt64(7)]);

        Assert.Equal(
            expected,
            ReferencePredicate.Matches(
                new Predicate.Compare(new FieldRef("population"), comparison, Literal.FromInteger(bound)),
                feature));
    }

    [Theory]
    [InlineData(false, true, -1)]
    [InlineData(true, true, 0)]
    [InlineData(7L, 8L, -1)]
    [InlineData(8L, 8L, 0)]
    [InlineData(2.5, 2.5, 0)]
    [InlineData("a", "b", -1)]
    public void Values_of_one_kind_order_by_payload(object left, object right, int expected)
    {
        Assert.Equal(
            Math.Sign(expected),
            Math.Sign(AttributeValueComparer.Instance.Compare(Value(left), Value(right))));
    }

    private static AttributeValue Value(object payload) => payload switch
    {
        bool flag => AttributeValue.FromBoolean(flag),
        long whole => AttributeValue.FromInt64(whole),
        double fraction => AttributeValue.FromDouble(fraction),
        string text => AttributeValue.FromString(text),
        _ => AttributeValue.Null,
    };

    [Fact]
    public void The_empty_reduction_is_one_group_of_nulls()
    {
        var specs = new List<AggregateSpec> { new(AggregateStatistic.Sum, "population", "total") };
        var group = FeatureReduction.EmptyGroup(specs);

        Assert.Empty(group.Key);
        Assert.Equal([AttributeValue.Null], group.Values);
    }

    [Fact]
    public void A_null_literal_satisfies_no_comparison()
    {
        var schema = new FeatureSchema([new FieldDefinition("name", AttributeKind.String, nullable: true)]);
        var feature = new Feature(new FeatureId("row"), schema, [AttributeValue.FromString("x")]);

        Assert.False(ReferencePredicate.Matches(
            new Predicate.Compare(new FieldRef("name"), ComparisonOperator.Equals, Literal.Null),
            feature));
    }
}
