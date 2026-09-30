using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The vocabulary's case-folding text comparison, measured as T-SQL
/// (ADR-0132): what a pushed <c>ILIKE</c> has to say for the pushed answer to
/// be the reference's answer.
///
/// <para>
/// The reference folds the pattern and the value with the ASCII alphabet and
/// then runs the same whole-value pattern test. T-SQL has no ASCII fold of its
/// own: <c>LOWER</c> is the server's linguistic case, and a
/// <c>Latin1_General_100_CI_AS</c> comparison folds <c>Épsilon</c> onto
/// <c>epsilon</c> — a fold the reference does not make, so the pushed row set
/// would be a superset of the reference's and the answer would depend on the
/// collation the database was created with. The comparison therefore writes the
/// fold itself (<c>TRANSLATE</c> over the ASCII alphabet) and states the byte
/// order the <c>LIKE</c> then runs in, which is the same binary collation
/// every other pushed string comparison carries.
/// </para>
///
/// <para>
/// The two changes are both needed: the fold alone would leave the comparison
/// under the database's case-insensitive default, and the collation alone would
/// leave the fold to the database.
/// </para>
/// </summary>
public sealed class SqlServerFoldedPatternTests
{
    private const string Fold = "TRANSLATE([code], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ', N'abcdefghijklmnopqrstuvwxyz') COLLATE Latin1_General_100_BIN2";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("code", AttributeKind.String, nullable: true),
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
    ]);

    private static string Where(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return SqlServerPredicateSql.Where(predicate!, Schema, []);
    }

    [Fact]
    public void A_folded_pattern_folds_the_column_and_states_the_byte_order_of_the_match()
    {
        // The shipped default collation is case-insensitive, so a folded
        // comparison that carried no collation of its own would fold the ASCII
        // alphabet twice over and fold everything else besides — a row set the
        // reference never selects.
        Assert.Equal($"{Fold} LIKE @p0", Where("code ILIKE 'ALPH%'"));
    }

    [Fact]
    public void The_folded_pattern_is_bound_folded()
    {
        // The parameter binds the folded pattern, which is the value the
        // reference evaluator folds the plan's literal to — the client's own
        // capitalisation still never reaches SQL (ADR-0028).
        var parameters = new List<object?>();
        Assert.True(FeatureFilterText.TryParse("code ILIKE 'ALP%'", out var predicate, out var error), error);

        SqlServerPredicateSql.Where(predicate!, Schema, parameters);

        Assert.Equal(["alp%"], parameters);
    }

    [Fact]
    public void A_folded_pattern_over_a_column_that_is_not_text_matches_nothing() =>
        // The reference evaluator's answer for a comparison with no meaning,
        // written the way this dialect writes a constant.
        Assert.Equal("(1 = 0)", Where("population ILIKE '3%'"));

    [Fact]
    public void A_folded_pattern_beside_a_byte_ordered_comparison_states_both_orders() =>
        Assert.Equal(
            $"{Fold} LIKE @p0 AND [code] COLLATE Latin1_General_100_BIN2 < @p1",
            Where("code ILIKE 'ALPH%' AND code < 'delta'"));

    [Fact]
    public void Every_text_field_is_folded_on_its_own_column() =>
        Assert.Equal(
            "TRANSLATE([label], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ', N'abcdefghijklmnopqrstuvwxyz') COLLATE Latin1_General_100_BIN2 LIKE @p0",
            Where("label ILIKE 'URBAN%'"));

    [Fact]
    public void The_byte_ordered_pattern_is_unchanged_and_still_a_pattern()
    {
        // Adding a comparison to the vocabulary must not move the one that was
        // already there: ADR-0123 made the byte-ordered `LIKE` state the byte
        // order, and it still does.
        Assert.Equal("[code] COLLATE Latin1_General_100_BIN2 LIKE @p0", Where("code LIKE 'ALPH%'"));
    }
}
