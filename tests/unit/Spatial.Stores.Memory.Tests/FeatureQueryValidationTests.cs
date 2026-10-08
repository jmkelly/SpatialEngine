using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The plan validation (<see cref="FeatureQueryValidation"/>): the unbounded
/// plan, the paging and identity guards, and every statistic rule — a count
/// is the only reduction over every row, an envelope needs a geometry field,
/// nothing else takes one, numerics need numeric fields and percentiles need
/// a fraction in [0, 1].
/// </summary>
public sealed class FeatureQueryValidationTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("score", AttributeKind.Double, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    [Fact]
    public void The_unbounded_plan_is_unbounded()
    {
        Assert.True(FeatureQuery.All.IsUnbounded);
        Assert.True(new FeatureQuery().IsUnbounded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Any_limit_bounds_the_plan(int limit) =>
        Assert.False(new FeatureQuery(Limit: limit).IsUnbounded);

    [Fact]
    public void Every_restriction_bounds_the_plan()
    {
        Assert.False(new FeatureQuery(Ids: [new FeatureId("a")]).IsUnbounded);
        Assert.False(new FeatureQuery(Where: new Predicate.Constant(true)).IsUnbounded);
        Assert.False(new FeatureQuery(BoundingBox: new BoundingBox(0, 0, 1, 1)).IsUnbounded);
        Assert.False(new FeatureQuery(Projection: ["name"]).IsUnbounded);
        Assert.False(new FeatureQuery(Order: [new OrderTerm("name")]).IsUnbounded);
        Assert.False(new FeatureQuery(Offset: 3).IsUnbounded);
        Assert.False(new FeatureQuery(Cursor: "abc").IsUnbounded);
    }

    [Fact]
    public void A_row_count_over_every_row_validates()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "total")]);
        FeatureQueryValidation.ValidateAggregate(Schema, query);
    }

    [Fact]
    public void Only_a_count_may_reduce_every_row()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Sum, AggregateSpec.AllFields, "total")]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryValidation.ValidateAggregate(Schema, query));
        Assert.Contains("only a count", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_envelope_needs_a_geometry_field()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "population", "box")]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryValidation.ValidateAggregate(Schema, query));
        Assert.Contains("needs a geometry field", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_geometry_field_takes_no_other_statistic()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Sum, "geometry", "total")]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryValidation.ValidateAggregate(Schema, query));
        Assert.Contains("cannot be aggregated", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_statistic_needs_a_numeric_field()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Sum, "name", "total")]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryValidation.ValidateAggregate(Schema, query));
        Assert.Contains("needs a numeric field", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percentile_needs_a_fraction_between_zero_and_one()
    {
        var query = new AggregateQuery(
            [new AggregateSpec(AggregateStatistic.PercentileContinuous, "score", "middle", Percentile: 2)]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryValidation.ValidateAggregate(Schema, query));
        Assert.Contains("percentile fraction", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_envelope_of_a_geometry_field_validates()
    {
        var query = new AggregateQuery([new AggregateSpec(AggregateStatistic.Envelope, "geometry", "box")]);
        FeatureQueryValidation.ValidateAggregate(Schema, query);
    }
}
