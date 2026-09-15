using Spatial.Core.Features;

namespace Spatial.Esri.Codec.Tests;

public sealed class EsriFilterConstantTests
{
    private static Literal Int(double value) => new(LiteralKind.Integer, null, value, false);

    private static Literal Decimal(double value) => new(LiteralKind.Decimal, null, value, false);

    private static Literal Text(string value) => new(LiteralKind.String, value, 0, false);

    private static Literal Bool(bool value) => new(LiteralKind.Boolean, null, 0, value);

    private static Literal Date(long epochMs) => new(LiteralKind.DateTime, null, epochMs, false);

    private static readonly Literal Null = new(LiteralKind.Null, null, 0, false);

    [Fact]
    public void Null_on_either_side_is_never_constant_true()
    {
        Assert.False(EsriFilterLogic.EvaluateConstant(Null, ComparisonOperator.Equals, Null));
        Assert.False(EsriFilterLogic.EvaluateConstant(Null, ComparisonOperator.Equals, Int(1)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(1), ComparisonOperator.NotEquals, Null));
    }

    [Fact]
    public void Dates_compare_by_epoch_milliseconds()
    {
        Assert.True(EsriFilterLogic.EvaluateConstant(Date(2000), ComparisonOperator.GreaterThan, Date(1000)));
        Assert.True(EsriFilterLogic.EvaluateConstant(Date(1000), ComparisonOperator.Equals, Date(1000)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Date(1000), ComparisonOperator.GreaterThan, Date(2000)));
    }

    [Fact]
    public void Mixed_date_and_non_date_operands_are_not_comparable()
    {
        Assert.False(EsriFilterLogic.EvaluateConstant(Date(1000), ComparisonOperator.Equals, Int(1000)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(1000), ComparisonOperator.Equals, Date(1000)));
    }

    [Fact]
    public void Booleans_support_only_equality()
    {
        Assert.True(EsriFilterLogic.EvaluateConstant(Bool(true), ComparisonOperator.Equals, Bool(true)));
        Assert.True(EsriFilterLogic.EvaluateConstant(Bool(true), ComparisonOperator.NotEquals, Bool(false)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Bool(true), ComparisonOperator.Equals, Bool(false)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Bool(true), ComparisonOperator.GreaterThan, Bool(false)));
    }

    [Fact]
    public void Boolean_mixed_with_other_kinds_is_not_comparable()
    {
        Assert.False(EsriFilterLogic.EvaluateConstant(Bool(true), ComparisonOperator.Equals, Int(1)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(1), ComparisonOperator.Equals, Bool(true)));
    }

    [Fact]
    public void Strings_compare_ordinally_and_match_like_patterns()
    {
        Assert.True(EsriFilterLogic.EvaluateConstant(Text("b"), ComparisonOperator.GreaterThan, Text("a")));
        Assert.True(EsriFilterLogic.EvaluateConstant(Text("a"), ComparisonOperator.Equals, Text("a")));
        Assert.False(EsriFilterLogic.EvaluateConstant(Text("a"), ComparisonOperator.LessThan, Text("a")));
        Assert.True(EsriFilterLogic.EvaluateConstant(Text("foobar"), ComparisonOperator.Like, Text("foo%")));
        Assert.False(EsriFilterLogic.EvaluateConstant(Text("foobar"), ComparisonOperator.Like, Text("baz%")));
    }

    [Fact]
    public void Numbers_compare_across_integer_and_decimal()
    {
        Assert.True(EsriFilterLogic.EvaluateConstant(Int(2), ComparisonOperator.GreaterThan, Int(1)));
        Assert.True(EsriFilterLogic.EvaluateConstant(Decimal(2.5), ComparisonOperator.Equals, Decimal(2.5)));
        Assert.True(EsriFilterLogic.EvaluateConstant(Int(2), ComparisonOperator.LessOrEqual, Decimal(2.5)));
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(2), ComparisonOperator.Equals, Decimal(2.5)));
    }

    [Fact]
    public void Numbers_mixed_with_other_kinds_are_not_comparable()
    {
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(1), ComparisonOperator.Equals, Text("1")));
        Assert.False(EsriFilterLogic.EvaluateConstant(Int(1), ComparisonOperator.Equals, Bool(true)));
    }
}

public sealed class EsriFilterAndFlattenTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
    ]);

    private static Feature Feature(string name, long population) => new(
        new FeatureId(name),
        Schema,
        [AttributeValue.FromString(name), AttributeValue.FromInt64(population)]);

    private static EsriFilterClause Parse(string text)
    {
        Assert.True(EsriFilterClause.TryParse(text, out var clause, out var error), error);
        return clause!;
    }

    [Fact]
    public void And_of_two_conjunctions_flattens_to_one_and_node()
    {
        var both = Parse("population > 1 AND population < 9")
            .And(Parse("name = 'Berlin' AND population > 2"));

        Assert.Equal(
            Parse("population > 1 AND population < 9 AND name = 'Berlin' AND population > 2").ToWhere(),
            both.ToWhere());
        Assert.True(both.Matches(Feature("Berlin", 3)));
        Assert.False(both.Matches(Feature("Berlin", 0)));
    }

    [Fact]
    public void And_appends_a_plain_clause_to_a_conjunction()
    {
        var both = Parse("population > 1 AND population < 9").And(Parse("name = 'Berlin'"));

        Assert.Equal(
            Parse("population > 1 AND population < 9 AND name = 'Berlin'").ToWhere(),
            both.ToWhere());
        Assert.True(both.Matches(Feature("Berlin", 3)));
        Assert.False(both.Matches(Feature("Paris", 3)));
    }

    [Fact]
    public void And_prepends_a_plain_clause_to_a_conjunction()
    {
        var both = Parse("population > 1").And(Parse("name = 'Berlin' AND population < 9"));

        Assert.Equal(
            Parse("population > 1 AND name = 'Berlin' AND population < 9").ToWhere(),
            both.ToWhere());
        Assert.True(both.Matches(Feature("Berlin", 3)));
        Assert.False(both.Matches(Feature("Berlin", 99)));
    }

    [Fact]
    public void And_of_two_plain_clauses_conjoins()
    {
        var both = Parse("population > 1").And(Parse("name = 'Berlin'"));

        Assert.Equal(Parse("population > 1 AND name = 'Berlin'").ToWhere(), both.ToWhere());
        Assert.True(both.Matches(Feature("Berlin", 3)));
        Assert.False(both.Matches(Feature("Berlin", 0)));
    }
}
