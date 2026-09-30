using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The generated attachment-sidecar T-SQL (ADR-0073 as it follows the shared
/// attachment face, ADR-0130): the sidecar's own identity columns are declared
/// under a byte-order collation, and a sidecar created by an earlier version is
/// re-collated on the way in, because <c>IF OBJECT_ID ... IS NULL CREATE
/// TABLE</c> will not re-declare a table that already exists.
/// </summary>
public sealed class SqlServerAttachmentQueriesTests
{
    /// <summary>
    /// The collation the sidecar declares. It is the same term
    /// <see cref="SqlServerPredicateSql.ByteOrderCollation"/> is, which is
    /// what ADR-0126 made every statement that names a *dataset's* identity
    /// state, so an attachment names its feature in the order the feature does.
    /// </summary>
    private const string ByteOrder = SqlServerPredicateSql.ByteOrderCollation;

    [Fact]
    public void A_new_sidecar_declares_its_identity_columns_under_a_byte_order_collation()
    {
        var sql = SqlServerQueries.EnsureAttachmentTable();

        Assert.Contains($"dataset nvarchar(300) COLLATE {ByteOrder} NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"feature_id nvarchar(300) COLLATE {ByteOrder} NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sidecar_created_by_an_earlier_version_is_recollated_on_the_way_in()
    {
        var sql = SqlServerQueries.RecollateAttachmentIdentity();

        // The step is guarded by the column's *current* collation, read from the
        // catalog, so it costs nothing for a sidecar this version created.
        Assert.Contains("sys.columns", sql, StringComparison.Ordinal);
        Assert.Contains("name = N'dataset'", sql, StringComparison.Ordinal);
        Assert.Contains("name = N'feature_id'", sql, StringComparison.Ordinal);
        Assert.Contains($"collation_name <> N'{ByteOrder}'", sql, StringComparison.Ordinal);

        // …and it re-declares exactly the two identity columns, under the same
        // collation the create statement declares.
        Assert.Contains($"ALTER COLUMN dataset nvarchar(300) COLLATE {ByteOrder} NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"ALTER COLUMN feature_id nvarchar(300) COLLATE {ByteOrder} NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recollate_touches_no_column_the_identity_does_not_name()
    {
        var sql = SqlServerQueries.RecollateAttachmentIdentity();

        Assert.DoesNotContain("ALTER COLUMN name ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN keywords ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN content ", sql, StringComparison.Ordinal);
    }
}
