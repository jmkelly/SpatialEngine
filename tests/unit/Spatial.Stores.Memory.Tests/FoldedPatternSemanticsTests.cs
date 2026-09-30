using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The semantics of the vocabulary's case-folding text comparison, pinned on
/// the reference evaluator (ADR-0074 §4, ADR-0132).
///
/// <para>
/// It is the one text comparison whose case behaviour every back end states the
/// same way, which is what makes a text filter pushable: the byte-ordered
/// <c>LIKE</c> reads case by whatever collation the store's database carries,
/// and a store that inherited it would answer a different set from this one on
/// the same plan. The fold is therefore <em>stated</em> rather than inherited:
/// the pattern and the value are both folded with the ASCII alphabet before the
/// same whole-value pattern test runs, and a pair outside that alphabet does
/// not fold — on any provider, which is the point of stating it.
/// </para>
///
/// <para>
/// Every store's pushdown is measured against these answers by the shared
/// conformance suite, so a dialect that folded a different set would go red
/// there rather than in production.
/// </para>
/// </summary>
public sealed class FoldedPatternSemanticsTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static Feature Row(string? name, long? population = 1) => new(
        new FeatureId("row"),
        Schema,
        [
            name is null ? AttributeValue.Null : AttributeValue.FromString(name),
            population is null ? AttributeValue.Null : AttributeValue.FromInt64(population.Value),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
        ]);

    private static bool Matches(string filter, Feature? feature = null)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return ReferencePredicate.Matches(predicate!, feature ?? Row("Berlin"));
    }

    [Theory]
    [InlineData("name ILIKE 'ber%'")]
    [InlineData("name ILIKE 'BER%'")]
    [InlineData("name ILIKE 'BeR%'")]
    public void A_folded_pattern_reads_either_side_in_either_case(string filter) =>
        // The fold is on the value and on the pattern: a client that typed the
        // search in capitals and a column that stores it in lower case are the
        // same test.
        Assert.True(Matches(filter));

    [Theory]
    [InlineData("name ILIKE '%erli%'", true)]
    [InlineData("name ILIKE '_ERLIN'", true)]
    [InlineData("name ILIKE 'ber%'", true)]
    [InlineData("name ILIKE 'ber_'", false)]
    [InlineData("name ILIKE 'lin'", false)]
    [InlineData("name ILIKE '%lin%'", true)]
    public void The_wildcards_are_the_pattern_ones_and_the_test_is_whole_value(string filter, bool expected) =>
        // Folding changes which case matches; it never changes what counts as a
        // whole value, so the pattern stays anchored at both ends.
        Assert.Equal(expected, Matches(filter));

    [Fact]
    public void A_byte_ordered_pattern_and_a_folded_one_answer_differently()
    {
        // The contrast that makes the comparison measurable at all: the same
        // pattern in the byte-ordered spelling selects nothing.
        Assert.False(Matches("name LIKE 'BER%'"));
        Assert.True(Matches("name ILIKE 'BER%'"));
    }

    [Fact]
    public void Two_features_one_byte_identity_are_one_set_under_a_folded_pattern()
    {
        // `delta` and `Delta` are two features under a byte comparison
        // (ADR-0098 §3, ADR-0126) and one set under a folded one. That is the
        // whole difference, and it is the reason the folding is a separate
        // comparison rather than a flag on the byte-ordered one: an identity
        // must keep its bytes, and only a search may fold them.
        Assert.True(Matches("name ILIKE 'DELTA'", Row("delta")));
        Assert.True(Matches("name ILIKE 'DELTA'", Row("Delta")));
        Assert.False(Matches("name = 'DELTA'", Row("delta")));
        Assert.False(Matches("name = 'DELTA'", Row("Delta")));
    }

    [Theory]
    [InlineData("Écoute", "écoute")]
    [InlineData("Αθήνα", "αθήνα")]
    [InlineData("ǳord", "ǲord")]
    public void The_fold_is_the_one_every_back_end_states(string value, string pattern)
    {
        // The fold is the ASCII alphabet and nothing else, because that is the
        // one fold every provider states identically: a server's own case
        // folding is a locale's, a locale differs between deployments, and a
        // pushdown whose answer depends on the deployment's collation is the
        // defect this comparison was added to end. So a case pair outside the
        // alphabet is not a match here, and is not a match in SQL either.
        Assert.False(Matches($"name ILIKE '{pattern}'", Row(value)));
        Assert.False(Matches($"name ILIKE '{value}'", Row(pattern)));

        // The value itself is still comparable: only the folding is ASCII, not
        // the text.
        Assert.True(Matches($"name ILIKE '{value}'", Row(value)));
    }

    [Fact]
    public void A_null_attribute_matches_no_pattern() =>
        // The same three-valued reading every other comparison gets: a null is
        // not a value a pattern can match.
        Assert.False(Matches("name ILIKE '%'", Row(null)));

    [Fact]
    public void A_null_literal_matches_nothing() =>
        Assert.False(Matches("name ILIKE NULL", Row("Berlin")));

    [Fact]
    public void A_column_that_is_not_text_matches_no_pattern() =>
        // A pattern over a number is a comparison with no meaning, which every
        // back end answers as "no rows" rather than coercing (ADR-0097 §2).
        Assert.False(Matches("population ILIKE '3%'", Row("Berlin", 3_664_000)));

    [Fact]
    public void A_folded_pattern_matches_every_non_null_value() =>
        Assert.True(Matches("name ILIKE '%'"));

    [Theory]
    [InlineData("name ILIKE 'BER%'")]
    [InlineData("name ilike 'BER%'")]
    [InlineData("name Ilike 'BER%'")]
    public void The_keyword_is_the_vocabularys_own_and_reads_in_either_case(string filter) =>
        // The filter text is parsed once at the boundary (ADR-0074 §3), so the
        // comparison is spelled once, here, and every back end compiles it.
        Assert.True(Matches(filter));

    [Fact]
    public void The_grammar_is_still_closed()
    {
        // An operator the vocabulary does not have is a parse error, not a
        // comparison a store is asked to guess at.
        Assert.False(FeatureFilterText.TryParse("name SOUNDSLIKE 'x'", out _, out var error));
        Assert.Contains("comparison operator", error, StringComparison.Ordinal);
    }
}
