using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// A string comparison in a pushed-down <c>WHERE</c>, measured as T-SQL
/// (ADR-0098 §3, ADR-0121, ADR-0123).
///
/// <para>
/// The contract compares strings ordinally, so <c>code &lt; 'delta'</c>
/// selects the rows below <c>delta</c> as bytes, and its <c>LIKE</c> matches a
/// pattern as bytes. SQL Server's shipped default collation is
/// <em>case-insensitive</em>, so a <c>WHERE</c> that inherits it folds
/// <c>Delta</c> onto <c>delta</c>: an ordering answers a different set, and a
/// <c>LIKE</c> matches a row the reference never selected. The comparison
/// therefore carries a binary collation, which is the same answer the Postgres
/// side states as <c>COLLATE "C"</c>.
///
/// <para>
/// The term is unconditional, unlike the Postgres side: a SQL Server database
/// has no collation this store reads, and the one it is given by default is
/// the folding one, so the only comparison that is always the contract's is the
/// one always written.
/// </para>
/// </summary>
public sealed class SqlServerPredicateCollationTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("code", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
    ]);

    [Theory]
    [InlineData("code < 'delta'", "[code] COLLATE Latin1_General_100_BIN2 < @p0")]
    [InlineData("code <= 'delta'", "[code] COLLATE Latin1_General_100_BIN2 <= @p0")]
    [InlineData("code = 'delta'", "[code] COLLATE Latin1_General_100_BIN2 = @p0")]
    [InlineData("code != 'delta'", "[code] COLLATE Latin1_General_100_BIN2 <> @p0")]
    public void A_text_comparison_states_the_byte_order(string filter, string expected) =>
        Assert.Equal(expected, Where(filter));

    [Fact]
    public void A_text_membership_states_the_byte_order_too()
    {
        // A case-insensitive collation folds `Delta` onto `delta`, so the list
        // would match the row the reference says it does not.
        Assert.Equal("[code] COLLATE Latin1_General_100_BIN2 IN (@p0, @p1)", Where("code IN ('a', 'b')"));
        Assert.Equal("[code] COLLATE Latin1_General_100_BIN2 NOT IN (@p0)", Where("code NOT IN ('a')"));
    }

    [Fact]
    public void A_text_pattern_states_the_byte_order_too()
    {
        // The reference matches a `LIKE` as an ordinal pattern, and a
        // case-folding collation would match a case the pattern did not name.
        Assert.Equal("[code] COLLATE Latin1_General_100_BIN2 LIKE @p0", Where("code LIKE 'a%'"));
    }

    [Theory]
    [InlineData("population > 1000", "[population] > @p0")]
    [InlineData("active = TRUE", "[active] = @p0")]
    [InlineData("seen > 1000", "[seen] > @p0")]
    [InlineData("code IS NULL", "[code] IS NULL")]
    public void A_comparison_of_anything_but_text_never_carries_a_collation(string filter, string expected) =>
        // T-SQL will not apply a text collation to a number, a date or a guid,
        // so the term follows the field's kind — and a null test is not a
        // comparison of values and has no collation to choose.
        Assert.Equal(expected, Where(filter));

    [Fact]
    public void A_grouped_filter_states_the_byte_order_on_the_text_half_only() =>
        Assert.Equal(
            "[population] > @p0 AND [code] COLLATE Latin1_General_100_BIN2 < @p1",
            Where("population > 1000 AND code < 'delta'"));

    [Fact]
    public void The_collation_is_the_binary_one_and_not_a_linguistic_name()
    {
        // A linguistic name here would be a second definition of the contract's
        // order, written in the database's alphabet instead of in bytes.
        Assert.Equal("Latin1_General_100_BIN2", SqlServerPredicateSql.ByteOrderCollation);
        Assert.Contains("_BIN2", SqlServerPredicateSql.ByteOrderCollation, StringComparison.Ordinal);
    }

    private static string Where(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return SqlServerPredicateSql.Where(predicate!, Schema, []);
    }
}
