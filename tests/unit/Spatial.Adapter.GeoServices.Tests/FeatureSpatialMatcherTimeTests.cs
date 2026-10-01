using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The shared <c>time</c> rule (spec §9.1.4, T-059): a feature matches when
/// any of its date attributes falls inside the inclusive extent, where a
/// <c>null</c> bound is infinite; a feature with no date values matches
/// unconditionally, because ArcGIS Server ignores <c>time</c> on layers
/// without time-aware fields.
/// </summary>
public sealed class FeatureSpatialMatcherTimeTests
{
    private const long Earlier = 1_000;

    private const long Inside = 2_000;

    private const long Later = 3_000;

    private static readonly FeatureSchema DatedSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
    ]);

    private static readonly FeatureSchema TwoDateSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("updated", AttributeKind.DateTimeOffset, nullable: true),
    ]);

    private static readonly FeatureSchema DatelessSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
    ]);

    [Fact]
    public void A_date_inside_the_extent_matches()
    {
        Assert.True(FeatureSpatialMatcher.MatchesTime(Dated(Inside), new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void The_extent_bounds_are_inclusive()
    {
        Assert.True(FeatureSpatialMatcher.MatchesTime(Dated(Earlier), new EsriTimeExtent(Earlier, Later)));
        Assert.True(FeatureSpatialMatcher.MatchesTime(Dated(Later), new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void A_date_before_the_extent_does_not_match()
    {
        Assert.False(FeatureSpatialMatcher.MatchesTime(Dated(Earlier - 1), new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void A_date_after_the_extent_does_not_match()
    {
        Assert.False(FeatureSpatialMatcher.MatchesTime(Dated(Later + 1), new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void A_missing_start_bound_is_infinite()
    {
        Assert.True(FeatureSpatialMatcher.MatchesTime(Dated(Earlier), new EsriTimeExtent(null, Later)));
    }

    [Fact]
    public void A_missing_end_bound_is_infinite()
    {
        Assert.True(FeatureSpatialMatcher.MatchesTime(Dated(Later), new EsriTimeExtent(Earlier, null)));
    }

    [Fact]
    public void A_null_date_attribute_is_not_a_dated_value()
    {
        var feature = new Feature(
            new FeatureId("null-dated"),
            DatedSchema,
            [AttributeValue.FromString("null-dated"), AttributeValue.Null]);

        Assert.True(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void A_feature_without_any_date_field_matches()
    {
        var feature = new Feature(
            new FeatureId("dateless"),
            DatelessSchema,
            [AttributeValue.FromString("dateless")]);

        Assert.True(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void Any_date_inside_the_extent_matches()
    {
        var feature = new Feature(
            new FeatureId("two-dates"),
            TwoDateSchema,
            [
                AttributeValue.FromString("two-dates"),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Earlier - 1)),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Inside)),
            ]);

        Assert.True(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later)));
    }

    [Fact]
    public void A_reversed_window_matches_nothing()
    {
        Assert.False(FeatureSpatialMatcher.MatchesTime(Dated(Inside), new EsriTimeExtent(Later, Earlier)));
    }

    /// <summary>
    /// A designated layer's rows are matched by the extent rule, not the bag
    /// rule: a row whose designated start is before the window and whose
    /// designated end is after it has no instant inside the window, and it
    /// still matches, because the window falls inside the row's extent
    /// (ADR-0175).
    /// </summary>
    [Fact]
    public void A_designated_row_that_straddles_the_extent_matches()
    {
        var feature = new Feature(
            new FeatureId("straddling"),
            TwoDateSchema,
            [
                AttributeValue.FromString("straddling"),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Earlier - 1)),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Later + 1)),
            ]);

        Assert.True(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later), Designation));
    }

    /// <summary>
    /// The designation is what changes the rule. A layer with none keeps the
    /// bag rule exactly, on the same row.
    /// </summary>
    [Fact]
    public void A_layer_with_no_designation_keeps_the_any_date_value_rule()
    {
        var feature = new Feature(
            new FeatureId("two-dates"),
            TwoDateSchema,
            [
                AttributeValue.FromString("two-dates"),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Earlier - 1)),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Later + 1)),
            ]);

        Assert.False(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later)));
        Assert.True(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later), new TemporalExtentFields("observed", "updated")));
    }

    /// <summary>
    /// A row whose designated start is after its designated end is an empty
    /// extent and matches nothing, rather than the interval its bounds are
    /// swapped into.
    /// </summary>
    [Fact]
    public void A_designated_row_with_reversed_bounds_matches_nothing()
    {
        var feature = new Feature(
            new FeatureId("reversed"),
            TwoDateSchema,
            [
                AttributeValue.FromString("reversed"),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Later + 1)),
                AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(Earlier - 1)),
            ]);

        Assert.False(FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(Earlier, Later), new TemporalExtentFields("observed", "updated")));
    }

    private static readonly TemporalExtentFields Designation = new("observed", "updated");

    private static Feature Dated(long milliseconds) => new(
        new FeatureId($"dated-{milliseconds}"),
        DatedSchema,
        [
            AttributeValue.FromString($"dated-{milliseconds}"),
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)),
        ]);
}
