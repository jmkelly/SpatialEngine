using Spatial.Core.Features;

namespace Spatial.Interop.Esri.Tests;

public sealed class EsriFilterClauseTests
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
    public void Equality_and_ordering_compare_typed_values()
    {
        Assert.True(Parse("name = 'Berlin'").Matches(Feature("Berlin", 3)));
        Assert.False(Parse("name = 'Paris'").Matches(Feature("Berlin", 3)));
        Assert.True(Parse("population > 1000").Matches(Feature("Berlin", 3664000)));
        Assert.False(Parse("population >= 4000000").Matches(Feature("Berlin", 3664000)));
    }

    [Fact]
    public void And_or_and_parentheses_nest()
    {
        var feature = Feature("Berlin", 3);
        Assert.True(Parse("name = 'Berlin' AND population = 3").Matches(feature));
        Assert.True(Parse("name = 'Paris' OR population = 3").Matches(feature));
        Assert.False(Parse("(name = 'Berlin' OR name = 'Paris') AND population = 4").Matches(feature));
    }

    [Fact]
    public void Like_uses_percent_and_underscore()
    {
        Assert.True(Parse("name LIKE 'Ber%'").Matches(Feature("Berlin", 1)));
        Assert.True(Parse("name LIKE '_erlin'").Matches(Feature("Berlin", 1)));
        Assert.False(Parse("name LIKE 'ber%'").Matches(Feature("Berlin", 1)));
    }

    [Fact]
    public void Null_tests_use_the_nullability()
    {
        var feature = new Feature(new FeatureId("1"), Schema, [AttributeValue.Null, AttributeValue.FromInt64(1)]);
        Assert.True(Parse("name IS NULL").Matches(feature));
        Assert.False(Parse("name IS NOT NULL").Matches(feature));
    }

    [Fact]
    public void An_unknown_field_is_a_typed_failure()
    {
        var exception = Assert.Throws<EsriInteropException>(() => Parse("missing = 1").Matches(Feature("Berlin", 1)));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public void To_where_round_trips_the_supported_grammar()
    {
        var clause = Parse("name = 'O''Brien' AND population > 10 OR name IS NULL");

        var where = clause.ToWhere();

        Assert.Equal("((name = 'O''Brien' AND population > 10) OR name IS NULL)", where);
        Assert.True(EsriFilterClause.TryParse(where, out _, out _));
    }

    [Fact]
    public void Not_equals_operators_are_supported()
    {
        Assert.True(Parse("name != 'Paris'").Matches(Feature("Berlin", 1)));
        Assert.True(Parse("name <> 'Paris'").Matches(Feature("Berlin", 1)));
    }

    [Theory]
    [InlineData("1=1; DROP TABLE features")]
    [InlineData("name = ")]
    [InlineData("name = 'unterminated")]
    [InlineData("LOWER(name) = 'x'")]
    [InlineData("population = 1.2.3")]
    public void Unsupported_constructs_are_rejected(string text)
    {
        Assert.False(EsriFilterClause.TryParse(text, out _, out _));
    }
}
