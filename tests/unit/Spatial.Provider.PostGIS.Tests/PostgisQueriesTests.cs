using Spatial.Core.Features;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Generated SQL (PostgisQueries): deterministic statements built from
/// validated identifiers and discovered columns — never from client text —
/// with all parameter placeholders positional and matching the bound-value
/// order, so injection-shaped input cannot become SQL structure.
/// </summary>
public sealed class PostgisQueriesTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("id", AttributeKind.Int64, false),
        ("name", AttributeKind.String, false),
        ("geom", AttributeKind.Geometry, false));

    [Fact]
    public void Select_quotes_the_qualified_name_and_wraps_geometry_in_ewkb()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        var sql = PostgisQueries.Select(dataset, Schema);

        Assert.Equal("SELECT \"id\", \"name\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\"", sql);
    }

    [Fact]
    public void Query_appends_the_predicate_after_where()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "SELECT \"id\", \"name\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\" WHERE \"name\" = @p0",
            PostgisQueries.Query(dataset, Schema, "\"name\" = @p0"));
    }

    [Fact]
    public void Insert_uses_one_parameter_per_batch_field_in_order()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"id\", \"name\", \"geom\") VALUES (@p0, @p1, ST_SetSRID(ST_GeomFromEWKB(@p2), 4326))",
            PostgisQueries.Insert(dataset, Schema, 4326));
    }

    [Fact]
    public void Create_table_types_columns_and_geometry_at_the_srid()
    {
        Assert.True(PostgisDatasetName.TryParse("public.result", out var dataset, out _));
        var schema = FeatureTests.Schema(
            ("id", AttributeKind.Int64, false),
            ("name", AttributeKind.String, false),
            ("geom", AttributeKind.Geometry, false),
            ("seen", AttributeKind.DateTimeOffset, true),
            ("token", AttributeKind.Guid, true),
            ("flag", AttributeKind.Boolean, false),
            ("score", AttributeKind.Double, false));

        var sql = PostgisQueries.CreateTable(dataset, schema, 3857);

        Assert.Equal(
            "CREATE TABLE \"public\".\"result\" (\"id\" bigint, \"name\" text, \"geom\" geometry(Geometry, 3857), "
            + "\"seen\" timestamptz, \"token\" uuid, \"flag\" boolean, \"score\" double precision)",
            sql);
    }

    [Fact]
    public void Catalogue_optional_pattern_is_a_bound_parameter()
    {
        var without = PostgisQueries.Catalogue(null);
        Assert.DoesNotContain("@p0", without);

        var with = PostgisQueries.Catalogue("roads_%");
        Assert.Contains("LIKE @p0", with);
        Assert.DoesNotContain("roads_%", with);
    }

    [Fact]
    public void Metadata_queries_use_positional_placeholders_and_never_client_text()
    {
        Assert.Contains("@p0", PostgisQueries.ColumnsMetadata());
        Assert.Contains("@p1", PostgisQueries.ColumnsMetadata());
        Assert.Contains("@p0", PostgisQueries.GeometryColumnsMetadata());
        Assert.Contains("@p0", PostgisQueries.PrimaryKeyColumns());
        Assert.Contains("@p0", PostgisQueries.RowEstimate());
        Assert.Contains("@p0", PostgisQueries.TableExists());
    }

    [Fact]
    public void Insert_returning_lists_the_identity_columns_or_stays_plain()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            PostgisQueries.Insert(dataset, Schema, 4326),
            PostgisQueries.InsertReturning(dataset, Schema, 4326, []));
        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"id\", \"name\", \"geom\") VALUES (@p0, @p1, ST_SetSRID(ST_GeomFromEWKB(@p2), 4326)) RETURNING \"id\"",
            PostgisQueries.InsertReturning(dataset, Schema, 4326, ["id"]));
    }

    [Fact]
    public void Insert_without_identity_omits_the_key_and_returns_it()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "INSERT INTO \"public\".\"places\" (\"name\", \"geom\") VALUES (@p0, ST_SetSRID(ST_GeomFromEWKB(@p1), 4326)) RETURNING \"id\"",
            PostgisQueries.InsertWithoutIdentity(dataset, Schema, 4326, ["id"]));
    }

    [Fact]
    public void Update_sets_every_field_and_targets_the_identity_parameter()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "UPDATE \"public\".\"places\" SET \"id\" = @p0, \"name\" = @p1, \"geom\" = ST_SetSRID(ST_GeomFromEWKB(@p2), 4326) WHERE \"id\" = @p0",
            PostgisQueries.Update(dataset, Schema, 4326, ["id"]));
    }

    [Fact]
    public void Delete_targets_the_identity_columns_with_positional_parameters()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "DELETE FROM \"public\".\"places\" WHERE \"tenant\" = @p0 AND \"id\" = @p1",
            PostgisQueries.Delete(dataset, ["tenant", "id"]));
    }

    [Fact]
    public void Select_by_identity_targets_the_identity_column_and_limits_a_single_lookup()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        Assert.Equal(
            "SELECT \"id\", \"name\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\" WHERE (\"id\" = @p0) LIMIT 1",
            PostgisQueries.SelectByIdentity(dataset, Schema, ["id"], 1));
    }

    [Fact]
    public void Select_by_identity_batches_or_groups_the_identity_tuple_in_input_order()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));

        var sql = PostgisQueries.SelectByIdentity(dataset, Schema, ["tenant", "id"], 2);

        Assert.Equal(
            "SELECT \"id\", \"name\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\" "
            + "WHERE (\"tenant\" = @p0 AND \"id\" = @p1) OR (\"tenant\" = @p2 AND \"id\" = @p3)",
            sql);
        Assert.DoesNotContain("LIMIT", sql);
    }
}
