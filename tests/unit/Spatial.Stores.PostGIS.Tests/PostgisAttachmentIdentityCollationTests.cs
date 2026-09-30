using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The attachment sidecar's own identity columns are compared by bytes
/// (ADR-0130): the table is the store's own, so it declares the order rather
/// than every statement that names it stating it again, and a sidecar created
/// by an earlier version — which <c>CREATE TABLE IF NOT EXISTS</c> will not
/// re-declare — is brought forward by an explicit re-collate.
/// </summary>
public sealed class PostgisAttachmentIdentityCollationTests
{
    [Fact]
    public void A_new_sidecar_declares_its_identity_columns_under_a_byte_order_collation()
    {
        var sql = PostgisQueries.EnsureAttachmentTable();

        Assert.Contains("\"dataset\" text COLLATE \"C\" NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"feature_id\" text COLLATE \"C\" NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sidecar_created_by_an_earlier_version_is_recollated_on_the_way_in()
    {
        var sql = PostgisQueries.RecollateAttachmentIdentity();

        // The step is guarded by the column's *current* collation, so it costs
        // one catalog read and no rewrite for a sidecar this version created.
        Assert.Contains("pg_collation", sql, StringComparison.Ordinal);
        Assert.Contains("attname = 'dataset'", sql, StringComparison.Ordinal);
        Assert.Contains("attname = 'feature_id'", sql, StringComparison.Ordinal);
        Assert.Contains("c.collname <> 'C'", sql, StringComparison.Ordinal);

        // …and it re-declares exactly the two identity columns, under the same
        // collation the create statement declares.
        Assert.Contains("ALTER COLUMN \"dataset\" TYPE text COLLATE \"C\"", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"feature_id\" TYPE text COLLATE \"C\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recollate_touches_no_column_the_identity_does_not_name()
    {
        var sql = PostgisQueries.RecollateAttachmentIdentity();

        Assert.DoesNotContain("ALTER COLUMN \"name\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN \"keywords\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN \"content\"", sql, StringComparison.Ordinal);
    }
}
