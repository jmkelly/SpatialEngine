using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;
using Spatial.Stores.SqlServer.Geometry;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// Pure schema discovery and row mapping (ADR-0073): catalogue rows to a
/// <see cref="DatasetDescription"/>, the geometry facts a dataset implies, and
/// boxed provider values to core features.
/// </summary>
public sealed class SqlServerSchemaDiscoveryTests
{
    private static SqlServerDatasetName Dataset => Parse("dbo.places");

    [Fact]
    public void Discovered_columns_become_the_scan_schema_in_column_order()
    {
        var facts = Facts();

        Assert.True(SqlServerSchemaDiscovery.TryBuild(Dataset, facts, Geometry, out var description, out var error), error);

        Assert.Equal("dbo.places", description.Id);
        Assert.Equal("dbo", description.SchemaName);
        Assert.Equal("places", description.Table);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(4326, description.Srid);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(7, description.EstimatedRowCount);
        Assert.Equal(
            [$"id|{AttributeKind.Int64}", $"name|{AttributeKind.String}", $"geom|{AttributeKind.Geometry}"],
            description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}").ToArray());
    }

    [Fact]
    public void A_geography_column_is_a_spatial_dataset_too()
    {
        var facts = new SqlServerSchemaDiscovery.SchemaFacts(
        [
            new SqlServerSchemaDiscovery.ColumnRow("id", "int", false, 1),
            new SqlServerSchemaDiscovery.ColumnRow("where", "geography", true, 2),
        ],
            ["id"],
            1);

        Assert.True(SqlServerSchemaDiscovery.TryBuild(Dataset, facts, Geometry, out var description, out _));

        Assert.Equal("where", description.GeometryColumn);
    }

    [Fact]
    public void A_table_with_no_geometry_column_is_not_a_spatial_dataset()
    {
        var facts = new SqlServerSchemaDiscovery.SchemaFacts(
            [new SqlServerSchemaDiscovery.ColumnRow("id", "int", false, 1)],
            ["id"],
            1);

        Assert.False(SqlServerSchemaDiscovery.TryBuild(Dataset, facts, Geometry, out _, out var error));

        Assert.Contains("not a spatial dataset", error);
    }

    [Fact]
    public void An_unsupported_column_names_the_column_and_the_type()
    {
        var facts = new SqlServerSchemaDiscovery.SchemaFacts(
        [
            new SqlServerSchemaDiscovery.ColumnRow("id", "int", false, 1),
            new SqlServerSchemaDiscovery.ColumnRow("label", "xml", true, 2),
            new SqlServerSchemaDiscovery.ColumnRow("geom", "geometry", true, 3),
        ],
            ["id"],
            1);

        Assert.False(SqlServerSchemaDiscovery.TryBuild(Dataset, facts, Geometry, out _, out var error));

        Assert.Contains("label", error);
        Assert.Contains("'xml'", error);
    }

    [Fact]
    public void A_table_that_does_not_exist_has_no_columns()
    {
        var facts = new SqlServerSchemaDiscovery.SchemaFacts([], [], 0);

        Assert.False(SqlServerSchemaDiscovery.TryBuild(Dataset, facts, Geometry, out _, out var error));

        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void Geometry_facts_prefer_the_sample_then_fall_back_to_the_recorded_srid()
    {
        var sampled = SqlServerSchemaDiscovery.FactsFrom(3857, "Point", 4326);
        Assert.Equal(3857, sampled.Srid);
        Assert.Equal("Point", sampled.GeometryType);

        var empty = SqlServerSchemaDiscovery.FactsFrom(DBNull.Value, DBNull.Value, 27700);
        Assert.Equal(27700, empty.Srid);
        Assert.Equal(SqlServerQueries.DefaultGeometryType, empty.GeometryType);

        // A planar (SRID 0) dataset keeps 0: the unknown-CRS marker of ADR-0028.
        Assert.Equal(0, SqlServerSchemaDiscovery.FactsFrom(0, "Point", 4326).Srid);
    }

    [Fact]
    public void A_catalogue_row_becomes_a_summary_with_its_geometry_facts()
    {
        var summary = SqlServerSchemaDiscovery.SummaryFromRow(["dbo", "places", "geom", 12L], Geometry);

        Assert.Equal("dbo.places", summary.Id);
        Assert.Equal("geom", summary.GeometryColumn);
        Assert.Equal(4326, summary.Srid);
        Assert.Equal(12, summary.EstimatedRowCount);
    }

    [Fact]
    public void A_row_maps_to_a_feature_with_its_identity_joined_by_a_pipe()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var wkb = SqlServerWkb.WriteGeometry(
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1.5, 2.5, CoordinateReference.Epsg(4326))), 4326);

        var feature = SqlServerRowMapper.MapRow(schema, [0], [7L, "Berlin", wkb], 0, 4326);

        Assert.Equal("7", feature.Id.Value);
        Assert.Equal(7, feature["id"].Int64Value);
        Assert.Equal("Berlin", feature["name"].StringValue);
        Assert.Equal(1.5, (double)Assert.IsType<Point>(feature["geom"].GeometryValue).X!, precision: 12);
    }

    [Fact]
    public void A_row_without_a_primary_key_falls_back_to_its_ordinal()
    {
        var schema = new FeatureSchema([new FieldDefinition("name", AttributeKind.String, true)]);

        Assert.Equal("3", SqlServerRowMapper.MapRow(schema, [], ["x"], 3, 4326).Id.Value);
    }

    [Fact]
    public void A_null_column_becomes_the_null_attribute()
    {
        var schema = new FeatureSchema([new FieldDefinition("name", AttributeKind.String, true)]);

        var feature = SqlServerRowMapper.MapRow(schema, [], [DBNull.Value], 0, 4326);

        Assert.True(feature["name"].IsNull);
    }

    [Fact]
    public void A_composite_identity_is_joined_with_a_pipe()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.Int64, false),
            new FieldDefinition("b", AttributeKind.String, false),
        ]);

        var feature = SqlServerRowMapper.MapRow(schema, [0, 1], [3L, "x"], 0, 4326);

        Assert.Equal("3|x", feature.Id.Value);
    }

    [Fact]
    public void Insert_parameters_convert_every_kind_through_the_interchange()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("flag", AttributeKind.Boolean, false),
            new FieldDefinition("count", AttributeKind.Int64, false),
            new FieldDefinition("amount", AttributeKind.Double, false),
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("seen", AttributeKind.DateTimeOffset, false),
            new FieldDefinition("tag", AttributeKind.Guid, false),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        var feature = new Feature(new FeatureId("1"), schema,
        [
            AttributeValue.FromBoolean(true),
            AttributeValue.FromInt64(2),
            AttributeValue.FromDouble(3.5),
            AttributeValue.FromString("four"),
            AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            AttributeValue.FromGuid(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
            AttributeValue.Null,
        ]);

        var values = SqlServerRowMapper.Parameters(schema, feature, 4326);

        Assert.Equal(true, values[0]);
        Assert.Equal(2L, values[1]);
        Assert.Equal(3.5d, values[2]);
        Assert.Equal("four", values[3]);
        Assert.Equal(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), values[5]);
        Assert.Null(values[6]);
    }

    private static SqlServerSchemaDiscovery.SchemaFacts Facts() =>
        new(
        [
            new SqlServerSchemaDiscovery.ColumnRow("geom", "geometry", true, 3),
            new SqlServerSchemaDiscovery.ColumnRow("id", "int", false, 1),
            new SqlServerSchemaDiscovery.ColumnRow("name", "nvarchar", true, 2),
        ],
            ["id"],
            7);

    private static SqlServerSchemaDiscovery.GeometryFacts Geometry => new(4326, "Point");

    private static SqlServerDatasetName Parse(string text)
    {
        Assert.True(SqlServerDatasetName.TryParse(text, out var name, out _));
        return name;
    }
}
