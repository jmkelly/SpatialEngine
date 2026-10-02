using Spatial.Core.Features;

namespace Spatial.Core.Tests;

/// <summary>
/// The feature temporal extent (ADR-0175): the interval between the two date
/// fields a layer's schema designates, with inclusive bounds and a null bound
/// read as infinite, and the three interval relations over it.
/// </summary>
public sealed class TemporalExtentTests
{
    private static readonly DateTimeOffset Before = DateTimeOffset.FromUnixTimeMilliseconds(1_000);

    private static readonly DateTimeOffset Inside = DateTimeOffset.FromUnixTimeMilliseconds(2_000);

    private static readonly DateTimeOffset After = DateTimeOffset.FromUnixTimeMilliseconds(3_000);

    private static readonly TemporalExtentFields Designation = new("begins", "ends");

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("begins", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("ends", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("recorded", AttributeKind.DateTimeOffset, nullable: true),
    ]);

    private static readonly FeatureSchema UndesignatedSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
    ]);

    [Fact]
    public void The_extent_is_the_interval_the_designation_names()
    {
        Assert.Equal(new TemporalExtent(Before, After), Extent(Row(Before, After, null)));
    }

    [Fact]
    public void An_absent_designated_bound_is_infinite()
    {
        var open = Extent(Row(Before, null, null));

        Assert.Equal(new TemporalExtent(Before, null), open);
        Assert.True(open.Overlaps(new TemporalExtent(null, null)));
    }

    [Fact]
    public void A_field_named_for_both_bounds_is_a_point()
    {
        var point = TemporalExtent.From(Row(Inside, null, null), new TemporalExtentFields("begins", "begins"));

        Assert.Equal(new TemporalExtent(Inside, Inside), point);
        Assert.True(point.Within(new TemporalExtent(Before, After)));
    }

    [Fact]
    public void A_row_with_both_designated_fields_null_is_the_whole_line()
    {
        var undated = Extent(Row(null, null, null));

        Assert.Equal(new TemporalExtent(null, null), undated);
        Assert.True(undated.Overlaps(new TemporalExtent(Before, After)));
        Assert.True(undated.Contains(new TemporalExtent(Before, After)));
        Assert.True(undated.Within(new TemporalExtent(Before, After)));
    }

    [Fact]
    public void An_instant_the_row_carries_outside_the_designation_is_not_an_extent_bound()
    {
        // The row also carries `recorded`, which nothing designates: the extent
        // is the designated pair or neither, never a min/max over the bag.
        var extent = Extent(Row(null, null, After));

        Assert.Equal(new TemporalExtent(null, null), extent);
        Assert.True(extent.Overlaps(new TemporalExtent(null, null)));
    }

    [Fact]
    public void A_designation_naming_a_field_the_schema_lacks_reads_as_no_bound()
    {
        var extent = TemporalExtent.From(Row(Before, After, null), new TemporalExtentFields("begins", "finishes"));

        Assert.Equal(new TemporalExtent(Before, null), extent);
    }

    [Fact]
    public void Overlaps_is_intersection_and_the_bounds_are_inclusive()
    {
        Assert.True(Extent(Row(Before, After, null)).Overlaps(new TemporalExtent(Inside, Inside)));
        Assert.True(Extent(Row(Before, Inside, null)).Overlaps(new TemporalExtent(Inside, After)));
        Assert.False(Extent(Row(Before, Inside, null)).Overlaps(new TemporalExtent(After, After)));
    }

    [Fact]
    public void Contains_is_the_extent_covering_the_window()
    {
        Assert.True(Extent(Row(Before, After, null)).Contains(new TemporalExtent(Inside, Inside)));
        Assert.False(Extent(Row(Before, Inside, null)).Contains(new TemporalExtent(Inside, After)));
    }

    [Fact]
    public void Within_is_the_window_covering_the_extent()
    {
        Assert.True(Extent(Row(Inside, Inside, null)).Within(new TemporalExtent(Before, After)));
        Assert.False(Extent(Row(Before, After, null)).Within(new TemporalExtent(Inside, After)));
    }

    [Fact]
    public void An_extent_whose_start_is_after_its_end_matches_no_relation()
    {
        var reversed = Extent(Row(After, Before, null));

        Assert.False(reversed.Overlaps(new TemporalExtent(null, null)));
        Assert.False(reversed.Contains(new TemporalExtent(null, null)));
        Assert.False(reversed.Within(new TemporalExtent(null, null)));
    }

    [Fact]
    public void A_null_request_bound_is_infinite()
    {
        var point = new TemporalExtent(Inside, Inside);

        Assert.True(point.Overlaps(new TemporalExtent(null, After)));
        Assert.True(point.Overlaps(new TemporalExtent(Before, null)));
        Assert.True(point.Contains(new TemporalExtent(null, null)));
        Assert.True(point.Within(new TemporalExtent(null, null)));
    }

    [Fact]
    public void The_window_may_be_built_from_epoch_milliseconds_with_null_bounds()
    {
        Assert.Equal(new TemporalExtent(Before, null), TemporalExtent.FromMilliseconds(1_000, null));
        Assert.Equal(new TemporalExtent(null, null), TemporalExtent.FromMilliseconds(null, null));
    }

    [Fact]
    public void An_undesignated_row_keeps_the_any_date_value_rule()
    {
        var feature = new Feature(
            new FeatureId("recorded-only"),
            UndesignatedSchema,
            [AttributeValue.FromString("recorded-only"), AttributeValue.FromDateTimeOffset(Before)]);

        Assert.True(TemporalExtent.MatchesAnyDate(feature, new TemporalExtent(Before, After)));
        Assert.False(TemporalExtent.MatchesAnyDate(feature, new TemporalExtent(Inside, After)));
    }

    [Fact]
    public void A_row_with_no_date_value_at_all_matches_the_any_date_rule_unconditionally()
    {
        var feature = new Feature(
            new FeatureId("dateless"),
            new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]),
            [AttributeValue.FromString("dateless")]);

        Assert.True(TemporalExtent.MatchesAnyDate(feature, new TemporalExtent(Before, After)));
    }

    private static TemporalExtent Extent(Feature feature) => TemporalExtent.From(feature, Designation);

    private static Feature Row(DateTimeOffset? begins, DateTimeOffset? ends, DateTimeOffset? recorded) => new(
        new FeatureId("row"),
        Schema,
        [
            AttributeValue.FromString("row"),
            Value(begins),
            Value(ends),
            Value(recorded),
        ]);

    private static AttributeValue Value(DateTimeOffset? moment) =>
        moment is { } value ? AttributeValue.FromDateTimeOffset(value) : AttributeValue.Null;
}
