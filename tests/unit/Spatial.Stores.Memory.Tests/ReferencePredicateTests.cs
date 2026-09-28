using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The reference evaluator of the one predicate vocabulary (ADR-0074 §4).
/// These are the semantics every store's pushdown has to agree with, so they
/// are pinned here on the in-memory store and, across the whole vocabulary,
/// by the shared conformance suite the PostGIS and SQL Server integration
/// suites run against the same cases.
/// </summary>
public sealed class MemoryPredicateTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("score", AttributeKind.Double, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("reference", AttributeKind.Guid, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static readonly Guid Known = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Seen = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private static Feature Row(
        string name = "Berlin",
        long population = 3_664_000,
        double score = 1.5,
        bool active = true,
        Guid? reference = null,
        DateTimeOffset? seen = null) => new(
        new FeatureId("row"),
        Schema,
        [
            name is null ? AttributeValue.Null : AttributeValue.FromString(name),
            population is long.MinValue ? AttributeValue.Null : AttributeValue.FromInt64(population),
            score is double.MinValue ? AttributeValue.Null : AttributeValue.FromDouble(score),
            active ? AttributeValue.FromBoolean(true) : AttributeValue.FromBoolean(false),
            reference is null ? AttributeValue.Null : AttributeValue.FromGuid(reference.Value),
            seen is null ? AttributeValue.Null : AttributeValue.FromDateTimeOffset(seen.Value),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
        ]);

    private static bool Matches(string filter, Feature? feature = null)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return ReferencePredicate.Matches(predicate!, feature ?? Row());
    }

    [Fact]
    public void Strings_compare_across_all_ordering_operators()
    {
        Assert.True(Matches("name = 'Berlin'"));
        Assert.True(Matches("name != 'Paris'"));
        Assert.True(Matches("name < 'C'"));
        Assert.True(Matches("name <= 'Berlin'"));
        Assert.True(Matches("name > 'A'"));
        Assert.True(Matches("name >= 'Berlin'"));
        Assert.False(Matches("name = 'Paris'"));
        Assert.False(Matches("name != 'Berlin'"));
        Assert.False(Matches("name < 'A'"));
        Assert.False(Matches("name > 'C'"));
    }

    [Fact]
    public void Numbers_compare_across_all_ordering_operators()
    {
        Assert.True(Matches("population = 3664000"));
        Assert.True(Matches("population != 1"));
        Assert.True(Matches("population < 4000000"));
        Assert.True(Matches("population <= 3664000"));
        Assert.True(Matches("population > 1000"));
        Assert.True(Matches("population >= 3664000"));
        Assert.False(Matches("population = 1"));
        Assert.False(Matches("population < 1000"));
        Assert.True(Matches("score > 1.4 AND score <= 1.5"));
    }

    [Fact]
    public void A_whole_number_wider_than_a_double_compares_exactly()
    {
        Assert.True(Matches("population = 9007199254740993", Row(population: 9_007_199_254_740_993L)));
        Assert.False(Matches("population = 9007199254740992", Row(population: 9_007_199_254_740_993L)));
    }

    [Fact]
    public void A_literal_of_another_kind_never_matches()
    {
        Assert.False(Matches("name = 5"));
        Assert.False(Matches("population = 'many'"));
        Assert.False(Matches("score = TRUE"));
        Assert.False(Matches("active = 'yes'"));
        Assert.False(Matches("reference = 5"));
    }

    [Fact]
    public void Booleans_and_guids_compare_by_equality_only()
    {
        Assert.True(Matches("active = TRUE"));
        Assert.False(Matches("active = FALSE"));
        Assert.True(Matches("active != FALSE"));
        Assert.False(Matches("active < TRUE"));
        Assert.True(Matches($"reference = '{Known}'", Row(reference: Known)));
        Assert.True(Matches($"reference <> '{Guid.Empty}'", Row(reference: Known)));
        Assert.False(Matches("reference = 'not-a-guid'", Row(reference: Known)));
    }

    [Fact]
    public void Dates_compare_by_epoch_milliseconds()
    {
        Assert.True(Matches("seen = TIMESTAMP '2023-11-14T22:13:20Z'", Row(seen: Seen)));
        Assert.True(Matches("seen > TIMESTAMP '2023-01-01 00:00:00' AND seen < TIMESTAMP '2024-01-01 00:00:00'", Row(seen: Seen)));
        Assert.False(Matches("seen >= TIMESTAMP '2024-01-01 00:00:00'", Row(seen: Seen)));
        Assert.True(Matches("seen > 1000", Row(seen: Seen)));
        Assert.True(Matches("seen <= 1700000000000", Row(seen: Seen)));
    }

    [Fact]
    public void A_null_attribute_satisfies_no_comparison()
    {
        var row = Row(population: long.MinValue);

        Assert.False(Matches("population = 1", row));
        Assert.False(Matches("population != 1", row));
        Assert.False(Matches("population > 1", row));
        Assert.False(Matches("population IN (1, 2)", row));
        Assert.False(Matches("population NOT IN (1, 2)", row));
    }

    [Fact]
    public void Null_tests_use_the_nullability()
    {
        Assert.True(Matches("name IS NULL", Row(name: null!)));
        Assert.False(Matches("name IS NOT NULL", Row(name: null!)));
        Assert.True(Matches("name IS NOT NULL"));
    }

    [Fact]
    public void A_null_literal_matches_nothing()
    {
        Assert.False(Matches("name = NULL"));
        Assert.False(Matches("name != NULL"));
    }

    [Fact]
    public void Like_is_a_whole_value_pattern_match()
    {
        Assert.True(Matches("name LIKE 'Ber%'"));
        Assert.True(Matches("name LIKE '_erlin'"));
        Assert.True(Matches("name LIKE '%lin%'"));
        Assert.False(Matches("name LIKE 'ber%'"));
        Assert.False(Matches("name LIKE 'Ber'"));
    }

    [Fact]
    public void Like_needs_a_string_field_and_a_string_pattern()
    {
        Assert.False(Matches("population LIKE '1%'"));
        Assert.False(Matches("name LIKE 1"));
    }

    [Fact]
    public void Membership_tests_any_and_its_negation()
    {
        Assert.True(Matches("name IN ('Berlin', 'Paris')"));
        Assert.False(Matches("name IN ('Paris', 'Rome')"));
        Assert.True(Matches("name NOT IN ('Paris')"));
        Assert.False(Matches("name NOT IN ('Berlin')"));
    }

    [Fact]
    public void Logical_operators_nest()
    {
        Assert.True(Matches("name = 'Berlin' AND population = 3664000"));
        Assert.True(Matches("name = 'Paris' OR population = 3664000"));
        Assert.False(Matches("(name = 'Berlin' OR name = 'Paris') AND population = 4"));
    }

    [Fact]
    public void The_match_all_and_match_none_constants_ignore_the_feature()
    {
        // The Esri `1=1` idiom compiles to a constant in the shared vocabulary
        // (ADR-0074 §7), and the store filter grammar has no other way to
        // spell it.
        var row = Row(population: long.MinValue, name: null!);

        Assert.True(ReferencePredicate.Matches(Predicate.All, row));
        Assert.False(ReferencePredicate.Matches(Predicate.None, row));
    }

    [Fact]
    public void An_unknown_field_is_a_typed_invalid_argument_failure()
    {
        Assert.True(FeatureFilterText.TryParse("missing = 1", out var predicate, out _));

        var exception = Assert.Throws<Spatial.Contracts.SpatialException>(() => ReferencePredicate.Matches(predicate!, Row()));

        Assert.Equal(Spatial.Contracts.SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'missing'", exception.Message);
    }
}
