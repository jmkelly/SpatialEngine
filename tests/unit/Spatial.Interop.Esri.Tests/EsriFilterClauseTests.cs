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
    public void A_synthetic_field_resolves_when_the_schema_lacks_it()
    {
        var feature = Feature("Berlin", 3);
        var synthetic = new EsriSyntheticField("OBJECTID", AttributeValue.FromInt64(42));

        Assert.True(Parse("OBJECTID = 42").Matches(feature, synthetic));
        Assert.False(Parse("OBJECTID = 7").Matches(feature, synthetic));
        Assert.True(Parse("OBJECTID IS NOT NULL").Matches(feature, synthetic));
        // The name is case-insensitive, matching the schema's field lookup.
        Assert.True(Parse("objectid > 40").Matches(feature, synthetic));
        // Without the synthetic field the clause is still an unknown-field failure.
        Assert.Throws<EsriInteropException>(() => Parse("OBJECTID = 42").Matches(feature));
    }

    [Fact]
    public void A_synthetic_field_takes_precedence_over_a_same_named_schema_field()
    {
        // The facade's synthetic OBJECTID is authoritative: the layer schema
        // skips a dataset column of that name, so the where clause must too.
        var feature = Feature("Berlin", 3);
        var synthetic = new EsriSyntheticField("name", AttributeValue.FromString("Paris"));

        Assert.True(Parse("name = 'Paris'").Matches(feature, synthetic));
        Assert.False(Parse("name = 'Berlin'").Matches(feature, synthetic));
    }

    [Fact]
    public void Strings_compare_across_all_ordering_operators()
    {
        var feature = Feature("Berlin", 1);

        Assert.True(Parse("name = 'Berlin'").Matches(feature));
        Assert.True(Parse("name != 'Paris'").Matches(feature));
        Assert.True(Parse("name < 'C'").Matches(feature));
        Assert.True(Parse("name <= 'Berlin'").Matches(feature));
        Assert.True(Parse("name > 'A'").Matches(feature));
        Assert.True(Parse("name >= 'Berlin'").Matches(feature));
        Assert.False(Parse("name = 'Paris'").Matches(feature));
        Assert.False(Parse("name != 'Berlin'").Matches(feature));
        Assert.False(Parse("name < 'A'").Matches(feature));
        Assert.False(Parse("name > 'C'").Matches(feature));
    }

    [Fact]
    public void Integers_compare_across_all_ordering_operators()
    {
        var feature = Feature("Berlin", 3664000);

        Assert.True(Parse("population = 3664000").Matches(feature));
        Assert.True(Parse("population != 1").Matches(feature));
        Assert.True(Parse("population < 4000000").Matches(feature));
        Assert.True(Parse("population <= 3664000").Matches(feature));
        Assert.True(Parse("population > 1000").Matches(feature));
        Assert.True(Parse("population >= 3664000").Matches(feature));
        Assert.False(Parse("population = 1").Matches(feature));
        Assert.False(Parse("population != 3664000").Matches(feature));
        Assert.False(Parse("population < 1000").Matches(feature));
        Assert.False(Parse("population > 4000000").Matches(feature));
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
    [InlineData("name = AND")]
    [InlineData("name IS 1")]
    [InlineData("(name = 'x'")]
    [InlineData("name @ 1")]
    public void Unsupported_constructs_are_rejected(string text)
    {
        Assert.False(EsriFilterClause.TryParse(text, out _, out _));
    }

    private static readonly FeatureSchema RichSchema = new(
    [
        new FieldDefinition("flag", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("id", AttributeKind.Guid, nullable: true),
        new FieldDefinition("when", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("score", AttributeKind.Double, nullable: true),
    ]);

    private static Feature Rich(AttributeValue flag, AttributeValue id, AttributeValue when, AttributeValue score) =>
        new(new FeatureId("rich"), RichSchema, [flag, id, when, score]);

    private static readonly Guid KnownGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Feature RichFeature() => Rich(
        AttributeValue.FromBoolean(true),
        AttributeValue.FromGuid(KnownGuid),
        AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        AttributeValue.FromDouble(1.5));

    [Fact]
    public void Booleans_and_guids_compare_by_equality_only()
    {
        var feature = RichFeature();

        Assert.True(Parse("flag = TRUE").Matches(feature));
        Assert.False(Parse("flag = FALSE").Matches(feature));
        Assert.True(Parse($"id = '{KnownGuid}'").Matches(feature));
        Assert.True(Parse($"id <> '{Guid.Empty}'").Matches(feature));
        Assert.False(Parse("flag <> TRUE").Matches(feature));
    }

    [Fact]
    public void A_literal_of_the_wrong_kind_makes_the_comparison_false()
    {
        var feature = RichFeature();

        Assert.False(Parse("flag = 'yes'").Matches(feature));
        Assert.False(Parse("flag = 1").Matches(feature));
        Assert.False(Parse("id = 'not-a-guid'").Matches(feature));
        Assert.False(Parse("id = 5").Matches(feature));
        Assert.False(Parse("score = TRUE").Matches(feature));
        Assert.False(Parse("when = 'later'").Matches(feature));
        Assert.False(Parse("name = 5").Matches(Feature("Berlin", 1)));
        Assert.False(Parse("population = 'many'").Matches(Feature("Berlin", 1)));
    }

    [Fact]
    public void Dates_compare_numerically_by_epoch_milliseconds()
    {
        var feature = RichFeature();

        Assert.True(Parse("when > 1000").Matches(feature));
        Assert.True(Parse("when <= 1700000000000").Matches(feature));
        Assert.False(Parse("when >= 1700000000001").Matches(feature));
    }

    [Fact]
    public void Doubles_compare_across_all_ordering_operators()
    {
        var feature = RichFeature();

        Assert.True(Parse("score = 1.5").Matches(feature));
        Assert.True(Parse("score != 2.5").Matches(feature));
        Assert.True(Parse("score < 2").Matches(feature));
        Assert.True(Parse("score <= 1.5").Matches(feature));
        Assert.True(Parse("score > 1").Matches(feature));
        Assert.True(Parse("score >= 1.5").Matches(feature));
    }

    [Theory]
    [InlineData("1=1", true)]
    [InlineData("1 = 1", true)]
    [InlineData("1=0", false)]
    [InlineData("1 <> 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("'a' = 'a'", true)]
    [InlineData("'a' = 'b'", false)]
    [InlineData("TRUE = TRUE", true)]
    [InlineData("FALSE = TRUE", false)]
    [InlineData("TRUE <> FALSE", true)]
    [InlineData("TRUE = 1", false)]
    public void Constant_predicates_are_evaluated_without_a_field(string text, bool expected)
    {
        // The Esri match-all / match-none idioms reference no column, so they
        // must not require a schema lookup (ArcGIS REST JS sends where=1=1 by
        // default).
        Assert.Equal(expected, Parse(text).Matches(Feature("Berlin", 1)));
    }

    [Fact]
    public void Constant_predicates_render_for_the_remote_where()
    {
        Assert.Equal("1 = 1", Parse("1=1").ToWhere());
        Assert.Equal("1 = 0", Parse("1=0").ToWhere());
        Assert.Equal("1 = 1", Parse("1 = 1").ToWhere());
    }

    [Fact]
    public void A_null_literal_never_matches()
    {
        Assert.False(Parse("name = NULL").Matches(Feature("Berlin", 1)));
    }

    [Fact]
    public void Like_requires_string_fields_and_patterns()
    {
        Assert.Throws<EsriInteropException>(() => Parse("population LIKE '1%'").Matches(Feature("Berlin", 1)));
        Assert.Throws<EsriInteropException>(() => Parse("name LIKE 1").Matches(Feature("Berlin", 1)));
    }

    [Fact]
    public void To_where_renders_every_operator_and_literal_shape()
    {
        Assert.Equal("name <= 'b'", Parse("name <= 'b'").ToWhere());
        Assert.Equal("name <> 'b'", Parse("name <> 'b'").ToWhere());
        Assert.Equal("population >= -5", Parse("population >= -5").ToWhere());
        Assert.Equal("score = 1.5", Parse("score = 1.5").ToWhere());
        Assert.Equal("flag = TRUE", Parse("flag = TRUE").ToWhere());
        Assert.Equal("name = NULL", Parse("name = NULL").ToWhere());
        Assert.Equal("name LIKE 'a%'", Parse("name LIKE 'a%'").ToWhere());
        Assert.Equal("name IS NOT NULL", Parse("name IS NOT NULL").ToWhere());
    }

    [Fact]
    public void Quoted_identifiers_are_lexed()
    {
        Assert.True(Parse("\"name\" = 'Berlin'").Matches(Feature("Berlin", 1)));
    }
}
