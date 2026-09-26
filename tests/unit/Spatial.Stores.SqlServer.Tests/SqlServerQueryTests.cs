using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The generated T-SQL (ADR-0073): every statement is built from validated
/// dataset identifiers and discovered column names, brackets its identifiers,
/// and never carries a client literal. Also the pure ingest plan and the
/// provider error mapping.
/// </summary>
public sealed class SqlServerQueryTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("name", AttributeKind.String, true),
        new FieldDefinition("geom", AttributeKind.Geometry, true),
    ]);

    private static SqlServerDatasetName Dataset => Parse("dbo.places");

    [Fact]
    public void A_select_brackets_its_columns_and_reads_geometry_as_wkb()
    {
        Assert.Equal(
            "SELECT [id], [name], [geom].STAsBinary() FROM [dbo].[places]",
            SqlServerQueries.Select(Dataset, Schema));
    }

    [Fact]
    public void A_query_appends_only_the_predicate_it_was_given()
    {
        Assert.Equal(SqlServerQueries.Select(Dataset, Schema), SqlServerQueries.Query(Dataset, Schema, null));
        Assert.Equal(
            "SELECT [id], [name], [geom].STAsBinary() FROM [dbo].[places] WHERE [id] = @p0",
            SqlServerQueries.Query(Dataset, Schema, "[id] = @p0"));
    }

    [Fact]
    public void Reading_by_identity_numbers_one_parameter_per_identity_value()
    {
        Assert.Equal(
            "SELECT [id], [name], [geom].STAsBinary() FROM [dbo].[places] "
            + "WHERE ([id] = @p0) OR ([id] = @p1)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["id"], 2));

        // One tuple: the primary-key predicate is already unique, so TOP 1
        // bounds the read without changing the result.
        Assert.Equal(
            "SELECT TOP 1 [id], [name], [geom].STAsBinary() FROM [dbo].[places] WHERE ([id] = @p0)",
            SqlServerQueries.SelectByIdentity(Dataset, Schema, ["id"], 1));
    }

    [Fact]
    public void An_insert_puts_the_output_clause_between_the_columns_and_the_values()
    {
        Assert.Equal(
            "INSERT INTO [dbo].[places] ([id], [name], [geom]) VALUES (@p0, @p1, geometry::STGeomFromWKB(@p2, 4326))",
            SqlServerQueries.Insert(Dataset, Schema, 4326));

        Assert.Equal(
            "INSERT INTO [dbo].[places] ([id], [name], [geom]) OUTPUT INSERTED.[id] "
            + "VALUES (@p0, @p1, geometry::STGeomFromWKB(@p2, 4326))",
            SqlServerQueries.InsertReturning(Dataset, Schema, 4326, ["id"]));
    }

    [Fact]
    public void An_omit_identity_insert_drops_the_identity_and_returns_it()
    {
        Assert.Equal(
            "INSERT INTO [dbo].[places] ([name], [geom]) OUTPUT INSERTED.[id] "
            + "VALUES (@p0, geometry::STGeomFromWKB(@p1, 4326))",
            SqlServerQueries.InsertWithoutIdentity(Dataset, Schema, 4326, ["id"], ["id"]));
    }

    [Fact]
    public void An_update_binds_the_identity_after_the_set_values()
    {
        Assert.Equal(
            "UPDATE [dbo].[places] SET [id] = @p0, [name] = @p1, [geom] = geometry::STGeomFromWKB(@p2, 4326) "
            + "WHERE [id] = @p3",
            SqlServerQueries.Update(Dataset, Schema, 4326, ["id"]));
    }

    [Fact]
    public void A_delete_and_an_existence_probe_agree_on_the_identity_tuple()
    {
        Assert.Equal("DELETE FROM [dbo].[places] WHERE [id] = @p0", SqlServerQueries.Delete(Dataset, ["id"]));
        Assert.Equal("SELECT TOP 1 1 FROM [dbo].[places] WHERE [id] = @p0", SqlServerQueries.FeatureExists(Dataset, ["id"]));
    }

    [Fact]
    public void A_create_table_declares_one_bracketed_column_per_field()
    {
        Assert.Equal(
            "CREATE TABLE [dbo].[places] ([id] bigint, [name] nvarchar(max), [geom] geometry)",
            SqlServerQueries.CreateTable(Dataset, Schema));
    }

    [Fact]
    public void The_catalogue_is_parameterised_only_by_the_optional_pattern()
    {
        Assert.DoesNotContain("@p0", SqlServerQueries.Catalogue(null));
        Assert.Contains("LIKE @p0", SqlServerQueries.Catalogue("%x%"));
    }

    [Fact]
    public void The_catalogue_samples_every_dataset_in_one_statement_with_an_ordinal()
    {
        var sql = SqlServerQueries.SampleAllGeometry(
        [
            new GeometrySample(Dataset, "geom"),
            new GeometrySample(Parse("dbo.roads"), "shape"),
        ]);

        Assert.Contains("(SELECT TOP 1 g.[geom].STSrid FROM [dbo].[places] AS g", sql);
        Assert.Contains("(SELECT TOP 1 g.[shape].STGeometryType() FROM [dbo].[roads] AS g", sql);
        Assert.Contains("UNION ALL", sql);
        Assert.EndsWith("ORDER BY ordinal", sql);
    }

    [Fact]
    public void The_provider_owned_sidecars_are_fixed_identifiers_only()
    {
        Assert.Contains("IF OBJECT_ID(N'spatial_datasets', N'U') IS NULL", SqlServerQueries.EnsureDatasetMetadataTable());
        Assert.Contains("varbinary(max)", SqlServerQueries.EnsureAttachmentTable());
        Assert.DoesNotContain("@p", SqlServerQueries.EnsureAttachmentTable());
    }

    [Fact]
    public void An_auto_ingest_adds_an_identity_primary_key()
    {
        var plan = Plan(new IngestRequest("dbo.places", 4326, IngestIdentity.Auto), 1);

        Assert.Equal(
            "CREATE TABLE [dbo].[places] ([name] nvarchar(max), [geom] geometry, [id] bigint IDENTITY(1,1) PRIMARY KEY)",
            plan.CreateTableSql());
        Assert.Equal("id", plan.IdentityColumn);
    }

    [Fact]
    public void A_source_ingest_keys_on_the_named_field_instead()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("code", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, false),
        ]);
        var plan = SqlServerIngestPlan.Create(
            new IngestRequest("dbo.places", 4326, IngestIdentity.Source, "code"),
            [new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema,
                [AttributeValue.FromInt64(1), AttributeValue.FromString("x")])])]);

        Assert.Equal("code", plan.IdentityColumn);
        Assert.EndsWith(", PRIMARY KEY ([code]))", plan.CreateTableSql());
    }

    [Fact]
    public void An_identity_free_ingest_creates_no_key_at_all()
    {
        var plan = Plan(new IngestRequest("dbo.places", 4326, IngestIdentity.None), 1);

        Assert.Null(plan.IdentityColumn);
        Assert.EndsWith("[geom] geometry)", plan.CreateTableSql());
    }

    [Fact]
    public void An_ingest_of_nothing_is_invalid_arguments()
    {
        var failure = Assert.Throws<SpatialException>(() =>
            SqlServerIngestPlan.Create(new IngestRequest("dbo.places", 4326), []));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public void A_non_positive_srid_is_invalid_arguments()
    {
        var failure = Assert.Throws<SpatialException>(() => Plan(new IngestRequest("dbo.places", 0), 1));

        Assert.Contains("SRID must be positive", failure.Message);
    }

    [Fact]
    public void An_auto_ingest_refuses_a_field_already_named_id()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.String, false)]);

        var failure = Assert.Throws<SpatialException>(() => SqlServerIngestPlan.Create(
            new IngestRequest("dbo.places", 4326, IngestIdentity.Auto),
            [new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromString("x")])])]));

        Assert.Contains("already has a field named 'id'", failure.Message);
    }

    [Fact]
    public void A_source_ingest_needs_an_integer_identity_field()
    {
        var schema = new FeatureSchema([new FieldDefinition("code", AttributeKind.String, false)]);
        var pages = new[] { new FeatureBatch(schema, [new Feature(new FeatureId("1"), schema, [AttributeValue.FromString("x")])]) };

        var missing = Assert.Throws<SpatialException>(() =>
            SqlServerIngestPlan.Create(new IngestRequest("dbo.places", 4326, IngestIdentity.Source, "nope"), pages));
        Assert.Contains("not a field of the ingest schema", missing.Message);

        var wrongKind = Assert.Throws<SpatialException>(() =>
            SqlServerIngestPlan.Create(new IngestRequest("dbo.places", 4326, IngestIdentity.Source, "code"), pages));
        Assert.Contains("must be Int64", wrongKind.Message);
    }

    [Fact]
    public void A_layout_sql_server_cannot_store_is_refused_at_plan_time()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var pages = new[]
        {
            new FeatureBatch(schema,
            [
                new Feature(new FeatureId("1"), schema,
                [
                    AttributeValue.FromString("x"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, 3, CoordinateReference.Epsg(4326))),
                ]),
            ]),
        };

        var failure = Assert.Throws<SpatialException>(() =>
            SqlServerIngestPlan.Create(new IngestRequest("dbo.places", 4326, IngestIdentity.None), pages));

        Assert.Contains("XYZ", failure.Message);
        Assert.Contains("XY coordinates only", failure.Message);
    }

    [Fact]
    public void A_second_page_with_another_schema_is_refused()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var other = new FeatureSchema([new FieldDefinition("name", AttributeKind.String, false)]);
        var pages = new[] { Page(schema, "1"), new FeatureBatch(other, []) };

        var failure = Assert.Throws<SpatialException>(() => SqlServerIngestPlan.Create(
            new IngestRequest("dbo.places", 4326, IngestIdentity.None), pages));

        Assert.Contains("does not share the first page's schema", failure.Message);
    }

    [Theory]
    [InlineData(SqlServerFailureCode.InvalidObjectName, SpatialException.NotFound)]
    [InlineData(SqlServerFailureCode.ConstraintViolation, SpatialException.InvalidArguments)]
    [InlineData(SqlServerFailureCode.UniqueIndexViolation, SpatialException.InvalidArguments)]
    [InlineData(SqlServerFailureCode.ObjectExists, SpatialException.InvalidArguments)]
    [InlineData(9999, SpatialException.InvalidArguments)]
    public void A_provider_error_number_maps_to_a_contract_code(int number, string expected) =>
        Assert.Equal(expected, SqlServerFailureCode.ForNumber(number));

    [Fact]
    public void Only_the_object_exists_number_means_the_dataset_is_already_there() =>
        Assert.True(SqlServerFailureCode.IsAlreadyCreatedNumber(SqlServerFailureCode.ObjectExists));

    private static SqlServerIngestPlan Plan(IngestRequest request, int features)
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        var pages = Enumerable.Range(0, features)
            .Select(i => Page(schema, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        return SqlServerIngestPlan.Create(request, pages);
    }

    private static FeatureBatch Page(FeatureSchema schema, string id) =>
        new(
            schema,
        [
            new Feature(new FeatureId(id), schema,
            [
                AttributeValue.FromString("x"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]),
        ]);

    private static SqlServerDatasetName Parse(string text)
    {
        Assert.True(SqlServerDatasetName.TryParse(text, out var name, out var reason), reason);
        return name;
    }
}
