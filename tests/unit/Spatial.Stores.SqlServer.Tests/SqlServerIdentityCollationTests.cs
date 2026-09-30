using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The comparison a statement makes against a dataset's <em>identity</em>
/// columns, measured as T-SQL (ADR-0098 §3, ADR-0121, ADR-0123, ADR-0126).
///
/// <para>
/// A text identity column under a case-folding collation is a case-insensitive
/// identity: the store asks for <c>delta</c> and the database answers with the
/// row whose key is <c>Delta</c> — a feature the contract distinguishes from
/// the one that was asked for, returned by a read, and written to by an edit.
/// Every statement that selects a row by identity therefore states the byte
/// order the contract compares strings in, on the same term and under the same
/// rule as a pushed attribute comparison (ADR-0123).
/// </para>
///
/// <para>
/// The term is unconditional here, as it is in the pushed <c>WHERE</c>: the
/// collation SQL Server ships by default is the case-folding one and nothing in
/// the store's own metadata says which collation a database carries. The table
/// the conformance fixture is keyed on declares
/// <c>Latin1_General_100_BIN2</c> on its key column — under the database's own
/// collation a text key cannot hold <c>Delta</c> and <c>delta</c> at all, and
/// the fixture has to be written before it is usable.
/// </para>
/// </summary>
public sealed class SqlServerIdentityCollationTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("code", AttributeKind.String, true),
        new FieldDefinition("geom", AttributeKind.Geometry, true),
    ]);

    private static readonly FeatureSchema Batch = new(
    [
        new FieldDefinition("code", AttributeKind.String, false),
        new FieldDefinition("geom", AttributeKind.Geometry, false),
    ]);

    private const string Columns = "[id], [code], [geom].STAsBinary()";

    private static SqlServerDatasetName Dataset
    {
        get
        {
            Assert.True(SqlServerDatasetName.TryParse("dbo.places", out var dataset, out _));
            return dataset;
        }
    }

    [Fact]
    public void A_text_identity_states_the_byte_order_of_a_lookup()
    {
        // `code = 'delta'` under the shipped case-insensitive collation is
        // `code = 'Delta'`, and TOP 1 then bounds the read to the wrong row.
        Assert.Equal(
            $"SELECT TOP 1 {Columns} FROM [dbo].[places] WHERE ([code] COLLATE Latin1_General_100_BIN2 = @p0)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["code"], 1));
    }

    [Fact]
    public void A_text_identity_states_the_byte_order_of_every_row_of_a_batch() =>
        Assert.Equal(
            $"SELECT {Columns} FROM [dbo].[places] "
            + "WHERE ([code] COLLATE Latin1_General_100_BIN2 = @p0) OR ([code] COLLATE Latin1_General_100_BIN2 = @p1)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["code"], 2));

    [Fact]
    public void An_identity_of_anything_but_text_never_carries_a_collation() =>
        // T-SQL will not apply a text collation to a number, a guid or a date,
        // so the term follows the identity column's kind.
        Assert.Equal(
            $"SELECT TOP 1 {Columns} FROM [dbo].[places] WHERE ([id] = @p0)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["id"], 1));

    [Fact]
    public void An_update_targets_a_text_identity_by_bytes()
    {
        // The row an update writes to is the row the identity named before the
        // edit, resolved against the *dataset's* schema rather than the
        // batch's: a batch need not carry the identity column at all.
        Assert.Equal(
            "UPDATE [dbo].[places] SET [code] = @p0, [geom] = geometry::STGeomFromWKB(@p1, 4326) "
            + "WHERE [code] COLLATE Latin1_General_100_BIN2 = @p2",
            SqlServerQueries.Update(Dataset, Batch, 4326, ["code"], Schema));
    }

    [Fact]
    public void A_delete_targets_a_text_identity_by_bytes() =>
        Assert.Equal(
            "DELETE FROM [dbo].[places] WHERE [code] COLLATE Latin1_General_100_BIN2 = @p0",
            SqlServerQueries.Delete(Dataset, Schema, ["code"]));

    [Fact]
    public void The_attachment_probe_matches_a_text_identity_by_bytes()
    {
        // The probe decides whether a feature exists, so a folding match stores
        // an attachment against a feature nobody asked for.
        Assert.Equal(
            "SELECT TOP 1 1 FROM [dbo].[places] WHERE [code] COLLATE Latin1_General_100_BIN2 = @p0",
            SqlServerQueries.FeatureExists(Dataset, Schema, ["code"]));
    }

    [Fact]
    public void An_identity_restriction_and_a_pushed_clause_state_the_same_order()
    {
        // Both halves of a plan's `WHERE` compare strings, and they are one
        // statement, so they cannot answer two different questions (ADR-0123).
        Assert.True(FeatureFilterText.TryParse("code < 'delta'", out var predicate, out var error), error);
        // The identity values are bound first, so the plan's own clause
        // continues its numbering after them.
        var parameters = new List<object?> { "delta" };
        var where = SqlServerPredicateSql.Where(predicate!, Schema, parameters);

        Assert.Equal(
            $"SELECT TOP 1 {Columns} FROM [dbo].[places] "
            + "WHERE ([code] COLLATE Latin1_General_100_BIN2 = @p0) AND ([code] COLLATE Latin1_General_100_BIN2 < @p1)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["code"], 1, where));
    }
}
