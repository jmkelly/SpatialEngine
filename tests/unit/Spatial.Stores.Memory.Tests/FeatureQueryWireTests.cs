using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The query-plan wire (<see cref="FeatureQueryWire"/>, ADR-0158 §4): every
/// predicate node and literal kind translates both ways, nodes carrying a
/// member their <c>op</c> does not define are refused, and the sugar spellings
/// fold onto the plan only when they agree with it.
/// </summary>
public sealed class FeatureQueryWireTests
{
    [Fact]
    public void Null_nodes_translate_to_null()
    {
        Assert.Null(FeatureQueryWire.ToPredicate(null));
        Assert.Null(FeatureQueryWire.ToPredicateDto(null));
    }

    [Fact]
    public void And_and_or_round_trip()
    {
        var plan = new Predicate.Every(
        [
            new Predicate.Compare(new FieldRef("a"), ComparisonOperator.Equals, Literal.FromInteger("1")),
            new Predicate.Some(
            [
                new Predicate.IsNull(new FieldRef("b"), Negated: true),
                new Predicate.Constant(true),
            ]),
        ]);

        var roundTripped = FeatureQueryWire.ToPredicate(FeatureQueryWire.ToPredicateDto(plan));

        Assert.Equal(Json(FeatureQueryWire.ToPredicateDto(plan)), Json(FeatureQueryWire.ToPredicateDto(roundTripped)));
    }

    [Fact]
    public void Is_in_round_trips_with_its_literals()
    {
        var plan = new Predicate.IsIn(
            new FieldRef("a"),
            [Literal.FromText("x"), Literal.FromInteger("3"), Literal.FromNumber(2.5), Literal.FromBoolean(true)],
            Negated: true);

        var roundTripped = FeatureQueryWire.ToPredicate(FeatureQueryWire.ToPredicateDto(plan));

        Assert.Equal(Json(FeatureQueryWire.ToPredicateDto(plan)), Json(FeatureQueryWire.ToPredicateDto(roundTripped)));
    }

    [Fact]
    public void Every_literal_kind_round_trips()
    {
        foreach (var literal in new[]
                 {
                     Literal.FromText("x"), Literal.FromInteger("9007199254740993"), Literal.FromNumber(1.5),
                     Literal.FromBoolean(false), Literal.FromMilliseconds(1_700_000_000_000), Literal.Null,
                 })
        {
            var plan = new Predicate.Compare(new FieldRef("a"), ComparisonOperator.Equals, literal);
            Assert.Equal(
                Json(FeatureQueryWire.ToPredicateDto(plan)),
                Json(FeatureQueryWire.ToPredicateDto(FeatureQueryWire.ToPredicate(FeatureQueryWire.ToPredicateDto(plan)))));
        }
    }

    [Fact]
    public void An_unknown_op_is_refused()
    {
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(new PredicateDto("negate")));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("Unknown predicate op", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_the_op_does_not_carry_is_refused()
    {
        var dto = new PredicateDto("isNull", Field: "a", Terms: [new PredicateDto("constant", Truth: true)]);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(dto));
        Assert.Contains("does not take 'terms'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_terms_list_is_refused()
    {
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(new PredicateDto("and", [])));
        Assert.Contains("needs a non-empty 'terms'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_is_in_is_refused()
    {
        var dto = new PredicateDto("isIn", Field: "a", Values: []);
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(dto));
        Assert.Contains("needs a non-empty 'values'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_constant_without_truth_is_refused()
    {
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(new PredicateDto("constant")));
        Assert.Contains("needs a boolean 'truth'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_carrying_a_member_its_kind_does_not_define_is_refused()
    {
        var dto = new PredicateDto(
            "compare",
            Field: "a",
            Operator: "Equals",
            Value: new LiteralDto(LiteralKind.String, Text: "x", Number: 1));
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(dto));
        Assert.Contains("does not take 'number'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_literal_without_its_flag_is_refused()
    {
        var dto = new PredicateDto(
            "compare",
            Field: "a",
            Operator: "Equals",
            Value: new LiteralDto(LiteralKind.Boolean));
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToPredicate(dto));
        Assert.Contains("needs a 'boolean'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicting_sugar_and_plan_spellings_are_refused()
    {
        var plan = new FeatureQueryDto(Where: new PredicateDto("constant", Truth: true));
        var failure = Assert.Throws<SpatialException>(() => FeatureQueryWire.ToQuery(plan, "a = 1", null));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'filter'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Agreeing_sugar_and_plan_spellings_are_used_once()
    {
        var plan = new FeatureQueryDto(Where: new PredicateDto("constant", Truth: true));
        var query = FeatureQueryWire.ToQuery(plan, null, null);
        Assert.Equal(
            Json(FeatureQueryWire.ToPredicateDto(new Predicate.Constant(true))),
            Json(FeatureQueryWire.ToPredicateDto(query.Where)));
    }

    [Fact]
    public void A_plan_without_sugar_reads_the_sugar_members()
    {
        var query = FeatureQueryWire.ToQuery(null, "a = 1", new BboxDto(0, 0, 1, 1));
        Assert.NotNull(query.Where);
        Assert.NotNull(query.BoundingBox);
    }

    [Fact]
    public void A_plan_round_trips_through_its_dto()
    {
        var query = new FeatureQuery(
            [new FeatureId("a")],
            new Predicate.Constant(true),
            new BoundingBox(0, 0, 1, 1),
            ["a"],
            [new OrderTerm("a", SortDirection.Descending)],
            10,
            null,
            "cursor");

        var roundTripped = FeatureQueryWire.ToQuery(FeatureQueryWire.ToDto(query), null, null);

        Assert.Equal(query.Ids, roundTripped.Ids);
        Assert.Equal(query.Where, roundTripped.Where);
        Assert.Equal(query.BoundingBox, roundTripped.BoundingBox);
        Assert.Equal(query.Projection, roundTripped.Projection);
        Assert.Equal(query.Order, roundTripped.Order);
        Assert.Equal(query.Limit, roundTripped.Limit);
        Assert.Equal(query.Cursor, roundTripped.Cursor);
    }

    private static string Json(PredicateDto? dto) => JsonSerializer.Serialize(dto);
}
