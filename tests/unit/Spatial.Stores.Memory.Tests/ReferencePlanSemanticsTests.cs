using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The reference semantics of a plan, pinned on their own (ADR-0098 §3). The
/// conformance suite compares a store's answer with the reference's, so a bug
/// in the reference would be invisible to it; these tests fix the reference's
/// own rules — null ordering, the identity tie-break, the paging walk, the
/// cursor's fingerprint, the projection, the empty-set reductions and the
/// envelope of a geometry field (ADR-0120) — so a provider's pushdown is
/// written against rules that are themselves pinned.
/// </summary>
public sealed class ReferencePlanSemanticsTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("score", AttributeKind.Int64, nullable: true),
    ]);

    private static Feature Row(long id, string name, long? score) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            score is null ? AttributeValue.Null : AttributeValue.FromInt64(score.Value),
        ]);

    /// <summary>Two tied scores, two nulls, and a name ordering that is the reverse of the ids.</summary>
    private static IReadOnlyList<Feature> Rows { get; } =
    [
        Row(3, "charlie", null),
        Row(1, "alpha", 10),
        Row(2, "bravo", 10),
        Row(4, "delta", null),
    ];

    private static string[] Ids(FeatureQueryPage page) => [.. page.Features.Select(feature => feature.Id.Value)];

    [Fact]
    public void Nulls_sort_last_ascending_and_first_descending()
    {
        Assert.Equal(["1", "2", "3", "4"], Ids(FeaturePlanExecutor.Execute(Schema, Rows, new FeatureQuery(Order: [new OrderTerm("score")]))));
        Assert.Equal(
            ["3", "4", "1", "2"],
            Ids(FeaturePlanExecutor.Execute(Schema, Rows, new FeatureQuery(Order: [new OrderTerm("score", SortDirection.Descending)]))));
    }

    [Fact]
    public void Tied_keys_are_separated_by_the_feature_identity_tie_break()
    {
        // Two rows that tie on the only requested key, in the opposite order to
        // their identities: the tie-break decides, and it decides by identity
        // ascending — not by the store's own order, and not by re-sorting on the
        // tie-break alone (which would discard the requested key).
        var tied = new[] { Row(5, "same", 1), Row(2, "same", 1), Row(9, "other", 2) };

        Assert.Equal(
            ["9", "2", "5"],
            Ids(FeaturePlanExecutor.Execute(Schema, tied, new FeatureQuery(Order: [new OrderTerm("name")]))));
    }

    /// <summary>
    /// Every requested key orders the plan, term by term: the second key is a
    /// tie-break over the first, never a replacement for it. The rows are the
    /// reverse case for it — the requested orders disagree with each other, and
    /// the descending score is not a total order on its own because two rows
    /// share a score, so an executor that re-sorts per key answers the last
    /// key's order (3, 4, 2, 1) where the contract's answer is the keys in
    /// sequence, then the identity (1, 2, 3, 4).
    /// </summary>
    [Fact]
    public void A_composite_order_applies_every_key_in_turn_and_then_the_identity()
    {
        var plan = new FeatureQuery(
            Order: [new OrderTerm("name"), new OrderTerm("score", SortDirection.Descending)]);

        Assert.Equal(["1", "2", "3", "4"], Ids(FeaturePlanExecutor.Execute(Schema, Rows, plan)));

        // Reversing the terms reverses the answer, which a re-sort per key
        // could not do: the last key alone would be the same order both ways.
        var reversed = plan with { Order = [new OrderTerm("score", SortDirection.Descending), new OrderTerm("name")] };
        Assert.Equal(["3", "4", "1", "2"], Ids(FeaturePlanExecutor.Execute(Schema, Rows, reversed)));
    }

    [Fact]
    public void A_plan_with_no_order_keeps_the_store_order()
    {
        Assert.Equal(["3", "1", "2", "4"], Ids(FeaturePlanExecutor.Execute(Schema, Rows, FeatureQuery.All)));
    }

    [Fact]
    public void The_page_cap_and_the_offset_select_rows_and_report_the_total()
    {
        var plan = new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2, Offset: 1);
        var page = FeaturePlanExecutor.Execute(Schema, Rows, plan);

        Assert.Equal(["2", "3"], Ids(page));
        Assert.Equal(4, page.TotalCount);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public void A_cursor_continues_the_plan_it_was_issued_for()
    {
        var plan = new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2);
        var first = FeaturePlanExecutor.Execute(Schema, Rows, plan);
        var second = FeaturePlanExecutor.Execute(Schema, Rows, plan with { Cursor = first.NextCursor, Offset = null });

        Assert.Equal(["1", "2"], Ids(first));
        Assert.Equal(["3", "4"], Ids(second));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void A_cursor_from_another_plan_is_invalid_arguments()
    {
        var plan = new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2);
        var cursor = FeaturePlanExecutor.Execute(Schema, Rows, plan).NextCursor!;
        var other = plan with { Order = [new OrderTerm("name")], Cursor = cursor, Offset = null };

        var failure = Assert.Throws<SpatialException>(() => FeaturePlanExecutor.Execute(Schema, Rows, other));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public void The_projection_returns_exactly_the_fields_it_asked_for()
    {
        var page = FeaturePlanExecutor.Execute(Schema, Rows, new FeatureQuery(Projection: ["name", "id"]));

        Assert.Equal(["name", "id"], page.Batches[0].Schema.Fields.Select(field => field.Name).ToArray());
        var first = page.Features.First();
        Assert.Equal("charlie", first["name"].StringValue);
        Assert.Equal(3, first["id"].Int64Value);
    }

    [Fact]
    public void An_ungrouped_reduction_of_an_empty_set_is_one_row_of_nulls()
    {
        var page = FeatureReduction.Aggregate(Schema, [], new AggregateQuery([new AggregateSpec(AggregateStatistic.Sum, "score", "total")]));

        var group = Assert.Single(page.Groups);
        Assert.Equal(AttributeValue.Null, group.Values[0]);
    }

    [Fact]
    public void A_grouped_reduction_of_an_empty_set_is_no_rows()
    {
        var page = FeatureReduction.Aggregate(
            Schema, [], new AggregateQuery([new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")], ["name"]));

        Assert.Empty(page.Groups);
    }

    [Fact]
    public void Nulls_are_skipped_and_a_statistic_with_no_value_is_null_not_zero()
    {
        var rows = new[] { Row(1, "a", 5), Row(2, "b", null) };
        var query = new AggregateQuery(
        [
            new AggregateSpec(AggregateStatistic.Count, "score", "scored"),
            new AggregateSpec(AggregateStatistic.Sum, "score", "total"),
            new AggregateSpec(AggregateStatistic.Average, "score", "mean"),
        ]);

        var values = Assert.Single(FeatureReduction.Aggregate(Schema, rows, query).Groups).Values;

        Assert.Equal(1, values[0].Int64Value);
        Assert.Equal(5, values[1].Int64Value);
        Assert.Equal(5, values[2].DoubleValue);

        // A group with no non-null value is null for every statistic, the count
        // included: the served statistics surface has always answered a
        // statistic with nothing to reduce as null (ADR-0057), and the
        // reference has to agree with it rather than with COUNT's zero.
        var nulls = Assert.Single(FeatureReduction.Aggregate(Schema, [rows[1]], query).Groups).Values;
        Assert.All(nulls, value => Assert.Equal(AttributeValue.Null, value));
    }

    [Fact]
    public void Variance_and_standard_deviation_are_the_sample_forms()
    {
        var rows = new[] { Row(1, "a", 2), Row(2, "b", 4), Row(3, "c", 4) };
        var query = new AggregateQuery(
        [
            new AggregateSpec(AggregateStatistic.Variance, "score", "var"),
            new AggregateSpec(AggregateStatistic.StdDev, "score", "sd"),
        ]);

        var values = Assert.Single(FeatureReduction.Aggregate(Schema, rows, query).Groups).Values;

        // The sample variance of {2, 4, 4} is 4/3, and the standard deviation
        // its square root — the population forms would be 2/3 and 0.816.
        Assert.Equal(4.0 / 3.0, values[0].DoubleValue, 10);
        Assert.Equal(Math.Sqrt(4.0 / 3.0), values[1].DoubleValue, 10);
    }

    /// <summary>
    /// The sample forms divide by n − 1, which is zero for a single value, so
    /// they are <em>undefined</em> there rather than zero: a store asked for
    /// <c>VAR_SAMP</c> answers null, and an answer of zero would be the
    /// population form of a group the sample form cannot describe. This is the
    /// rule the pushdown is measured against, so it is stated here rather than
    /// left to whichever reduction runs first.
    /// </summary>
    [Fact]
    public void The_sample_variance_of_fewer_than_two_values_is_null_and_not_zero()
    {
        var query = new AggregateQuery(
        [
            new AggregateSpec(AggregateStatistic.Variance, "score", "var"),
            new AggregateSpec(AggregateStatistic.StdDev, "score", "sd"),
        ]);

        var one = Assert.Single(FeatureReduction.Aggregate(Schema, [Row(1, "a", 5)], query).Groups).Values;
        Assert.All(one, value => Assert.Equal(AttributeValue.Null, value));

        var none = Assert.Single(FeatureReduction.Aggregate(Schema, [], query).Groups).Values;
        Assert.All(none, value => Assert.Equal(AttributeValue.Null, value));
    }

    /// <summary>
    /// The envelope of a geometry field is the smallest rectangle over the
    /// group's non-null geometries (ADR-0120) — the reduction a layer's extent
    /// is, which is why the nulls are skipped rather than counted as the origin
    /// and why a group with no geometry at all is the same null every other
    /// statistic with nothing to reduce gives.
    /// </summary>
    [Fact]
    public void The_envelope_of_a_geometry_field_is_the_smallest_rectangle_over_its_non_null_geometries()
    {
        var page = FeatureReduction.Aggregate(
            Places,
            PlaceRows,
            new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "place", "box")], ["name"]));

        // Group "a" holds two points and a null; group "b" holds one point and
        // a null, so its rectangle is that point's own.
        Assert.Equal(new Envelope(1, 5, 2, 9), page.Groups[0].Values[0].EnvelopeValue);
        Assert.Equal(new Envelope(3, 3, 3, 3), page.Groups[1].Values[0].EnvelopeValue);
        Assert.Equal(AttributeKind.Envelope, page.Groups[0].Values[0].Kind);
    }

    [Fact]
    public void The_envelope_of_a_group_with_no_geometry_is_null_and_the_kind_is_the_reduced_one()
    {
        var spec = new AggregateSpec(AggregateStatistic.Envelope, "place", "box");
        var query = new AggregateQuery([spec]);

        // A group whose every geometry is null, and a reduction of no rows at
        // all: both are "no values to reduce", which is a null and not the
        // empty rectangle.
        var nulls = Assert.Single(FeatureReduction.Aggregate(Places, [Place(1, "a", null)], query).Groups).Values;
        Assert.Equal(AttributeValue.Null, nulls[0]);
        Assert.Equal(AttributeValue.Null, Assert.Single(FeatureReduction.Aggregate(Places, [], query).Groups).Values[0]);

        Assert.Equal(AttributeKind.Envelope, FeatureReduction.ResultKind(spec, AttributeKind.Geometry));
    }

    /// <summary>
    /// The envelope reduces geometries and takes nothing else, exactly as every
    /// other statistic takes the kinds it can reduce: a sum of a geometry column
    /// and an envelope of a numeric one are both requests no store can answer,
    /// so they are refused at the boundary rather than guessed at.
    /// </summary>
    [Fact]
    public void An_envelope_needs_a_geometry_field_and_no_other_statistic_takes_one()
    {
        var overNumbers = new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "score", "box")]);
        var failure = Assert.Throws<SpatialException>(() => FeatureReduction.Aggregate(Schema, Rows, overNumbers));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("an envelope needs a geometry field", failure.Message, StringComparison.Ordinal);

        var overGeometry = new AggregateQuery([new AggregateSpec(AggregateStatistic.Sum, "place", "total")]);
        var refused = Assert.Throws<SpatialException>(() => FeatureReduction.Aggregate(Places, PlaceRows, overGeometry));
        Assert.Equal(SpatialException.InvalidArguments, refused.Code);
        Assert.Contains("a geometry field cannot be aggregated", refused.Message, StringComparison.Ordinal);
    }

    private static readonly FeatureSchema Places = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        new FieldDefinition("place", AttributeKind.Geometry, nullable: true),
    ]);

    private static Feature Place(long id, string name, (double X, double Y)? at) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Places,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            AttributeValue.Null,
            at is { } point
                ? AttributeValue.FromGeometry(GeometryFactory.CreatePoint(point.X, point.Y))
                : AttributeValue.Null,
        ]);

    private static IReadOnlyList<Feature> PlaceRows { get; } =
    [
        Place(1, "a", (1, 5)),
        Place(2, "a", null),
        Place(3, "a", (2, 9)),
        Place(4, "b", (3, 3)),
        Place(5, "b", null),
    ];
}
