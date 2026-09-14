using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Generated attachment SQL (T-088, ADR-0065 §2): the sidecar-table
/// statements are built from fixed identifiers with every dataset, feature
/// and attachment value as a bound parameter — never client text — so the
/// red-first contract pins deterministic text exactly like the feature
/// statements in <see cref="PostgisQueriesTests"/>.
/// </summary>
public sealed class PostgisAttachmentQueriesTests
{
    [Fact]
    public void Ensure_creates_the_sidecar_table_with_bytea_content_only_if_missing()
    {
        var sql = PostgisQueries.EnsureAttachmentTable();

        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS \"public\".\"spatial_attachments\" (\"dataset\" text NOT NULL, \"feature_id\" text NOT NULL, "
            + "\"attachment_id\" bigint NOT NULL, \"name\" text NOT NULL, \"content_type\" text NOT NULL, "
            + "\"size_bytes\" bigint NOT NULL, \"keywords\" text NULL, \"content\" bytea NOT NULL, "
            + "PRIMARY KEY (\"dataset\", \"feature_id\", \"attachment_id\"))",
            sql);
    }

    [Fact]
    public void Max_reads_the_per_feature_high_water_mark_as_a_bound_lookup()
    {
        var sql = PostgisQueries.MaxAttachmentId();

        Assert.Equal(
            "SELECT COALESCE(MAX(\"attachment_id\"), 0) FROM \"public\".\"spatial_attachments\" "
            + "WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1",
            sql);
    }

    [Fact]
    public void Insert_binds_every_column_positionally()
    {
        var sql = PostgisQueries.InsertAttachment();

        Assert.Equal(
            "INSERT INTO \"public\".\"spatial_attachments\" (\"dataset\", \"feature_id\", \"attachment_id\", \"name\", "
            + "\"content_type\", \"size_bytes\", \"keywords\", \"content\") VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)",
            sql);
    }

    [Fact]
    public void List_orders_descriptors_by_attachment_id()
    {
        var sql = PostgisQueries.ListAttachments();

        Assert.Equal(
            "SELECT \"attachment_id\", \"name\", \"content_type\", \"size_bytes\", \"keywords\" "
            + "FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 ORDER BY \"attachment_id\"",
            sql);
    }

    [Fact]
    public void Get_returns_the_descriptor_columns_plus_the_bytea_content()
    {
        var sql = PostgisQueries.GetAttachment();

        Assert.Equal(
            "SELECT \"attachment_id\", \"name\", \"content_type\", \"size_bytes\", \"keywords\", \"content\" "
            + "FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2",
            sql);
    }

    [Fact]
    public void Update_replaces_every_mutable_column_for_one_attachment()
    {
        var sql = PostgisQueries.UpdateAttachment();

        Assert.Equal(
            "UPDATE \"public\".\"spatial_attachments\" SET \"name\" = @p3, \"content_type\" = @p4, \"size_bytes\" = @p5, "
            + "\"keywords\" = @p6, \"content\" = @p7 WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2",
            sql);
    }

    [Fact]
    public void Delete_targets_one_attachment_with_positional_parameters()
    {
        var sql = PostgisQueries.DeleteAttachment();

        Assert.Equal(
            "DELETE FROM \"public\".\"spatial_attachments\" WHERE \"dataset\" = @p0 AND \"feature_id\" = @p1 AND \"attachment_id\" = @p2",
            sql);
    }

    [Fact]
    public void Feature_exists_probes_the_identity_tuple_with_a_limit()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "SELECT 1 FROM \"public\".\"places\" WHERE \"id\" = @p0 LIMIT 1",
            PostgisQueries.FeatureExists(dataset, ["id"]));
    }

    [Fact]
    public void Feature_exists_conjoins_a_composite_identity_in_order()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "SELECT 1 FROM \"public\".\"places\" WHERE \"tenant\" = @p0 AND \"id\" = @p1 LIMIT 1",
            PostgisQueries.FeatureExists(dataset, ["tenant", "id"]));
    }
}
