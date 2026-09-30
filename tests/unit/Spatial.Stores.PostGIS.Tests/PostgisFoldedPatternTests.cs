using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The vocabulary's case-folding text comparison, measured as SQL
/// (ADR-0132): what a pushed <c>ILIKE</c> has to say for the pushed answer to
/// be the reference's answer.
///
/// <para>
/// The reference folds the pattern and the value with the ASCII alphabet and
/// then runs the same whole-value pattern test, so the dialect has to fold the
/// same way on <em>every</em> database — and a locale database's own folding is
/// a locale's. <c>ILIKE</c> is exactly that: it folds under the database's
/// collation, so it is <c>Épsilon</c> for <c>epsilon</c> on a stock container
/// and byte-exact on a <c>C</c> one. The comparison therefore writes its own
/// fold with <c>translate</c> and states the byte order the <c>LIKE</c> then
/// runs in, rather than inheriting either.
/// </para>
///
/// <para>
/// Every case here is free of Npgsql and of a container; the value the store
/// reads from the container is proved by
/// <c>PostgisPredicateConformanceTests</c>, which runs the shared suite over a
/// table whose restriction really is pushed down.
/// </para>
/// </summary>
public sealed class PostgisFoldedPatternTests
{
    private const string Fold = "translate(\"code\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("code", AttributeKind.String, nullable: true),
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
    ]);

    private static string Where(string filter, bool byteOrderText)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return PostgisPredicateSql.Where(predicate!, Schema, byteOrderText, []);
    }

    [Fact]
    public void A_folded_pattern_folds_the_column_and_states_the_byte_order_of_the_match()
    {
        // `LIKE` is case-sensitive whatever the database's collation is, so the
        // folded operand is all the case behaviour there is — and a plain
        // `LIKE` on an unfolded column would answer the byte comparison.
        Assert.Equal($"{Fold} LIKE @p0", Where("code ILIKE 'ALPH%'", byteOrderText: false));
    }

    [Fact]
    public void The_byte_order_probe_does_not_change_the_folded_comparison()
    {
        // ADR-0123 reads the database's collation to decide whether a string
        // comparison needs the byte-order term. A folded comparison states its
        // own fold, so it takes no term either way and the plan is the same SQL
        // on a locale database and on a `C` one.
        Assert.Equal(
            Where("code ILIKE 'ALPH%'", byteOrderText: false),
            Where("code ILIKE 'ALPH%'", byteOrderText: true));
    }

    [Fact]
    public void A_folded_comparison_does_not_make_the_plan_read_the_collation()
    {
        // The catalog read is paid for by a plan that compares text *by bytes*.
        // A plan whose only text comparison states its own fold cannot use the
        // answer, so it should not pay a round trip for it.
        var folded = Parse("code ILIKE 'ALPH%'");

        Assert.False(PostgisPredicateSql.ComparesText(folded, Schema));
        Assert.True(PostgisPredicateSql.ComparesText(Parse("code < 'delta'"), Schema));
        Assert.True(PostgisPredicateSql.ComparesText(Parse("code ILIKE 'ALPH%' AND code < 'delta'"), Schema));
        Assert.False(PostgisPredicateSql.ComparesText(Parse("population > 1000"), Schema));
    }

    [Fact]
    public void The_folded_pattern_is_bound_folded()
    {
        // The parameter binds the folded pattern, which is the same value the
        // reference evaluator folds the plan's literal to — the client text
        // itself still never reaches SQL as structure (ADR-0028).
        var parameters = new List<object?>();
        Assert.True(FeatureFilterText.TryParse("code ILIKE 'ALP%'", out var predicate, out var error), error);

        PostgisPredicateSql.Where(predicate!, Schema, byteOrderText: false, parameters);

        Assert.Equal(["alp%"], parameters);
    }

    [Fact]
    public void A_folded_pattern_over_a_column_that_is_not_text_matches_nothing() =>
        // The reference evaluator's answer for a comparison with no meaning,
        // written as the constant every backend can answer it with.
        Assert.Equal("FALSE", Where("population ILIKE '3%'", byteOrderText: false));

    [Fact]
    public void A_folded_pattern_beside_a_byte_ordered_comparison_states_both_orders()
    {
        // The two orderings live side by side in one statement, and each is
        // stated where it applies: the fold on the pattern test, the byte order
        // on the comparison. A plan that mixed them up would answer a
        // different row set from the reference for the same tree.
        Assert.Equal(
            $"{Fold} LIKE @p0 AND \"code\" COLLATE \"C\" < @p1",
            Where("code ILIKE 'ALPH%' AND code < 'delta'", byteOrderText: false));
    }

    [Fact]
    public void Every_text_field_is_folded_on_its_own_column()
    {
        Assert.Equal(
            "translate(\"label\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') LIKE @p0",
            Where("label ILIKE 'URBAN%'", byteOrderText: false));
    }

    private static Predicate Parse(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return predicate!;
    }
}
