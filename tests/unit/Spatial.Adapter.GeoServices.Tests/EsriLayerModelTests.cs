using System.Text.Json;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

public sealed class EsriLayerModelTests
{
    [Theory]
    [InlineData("point", "esriGeometryPoint")]
    [InlineData("POINT", "esriGeometryPoint")]
    [InlineData("  point  ", "esriGeometryPoint")]
    [InlineData("multipoint", "esriGeometryMultipoint")]
    [InlineData("linestring", "esriGeometryPolyline")]
    [InlineData("multilinestring", "esriGeometryPolyline")]
    [InlineData("line", "esriGeometryPolyline")]
    [InlineData("linearring", "esriGeometryPolyline")]
    [InlineData("polygon", "esriGeometryPolygon")]
    [InlineData("multipolygon", "esriGeometryPolygon")]
    [InlineData("unknown", "esriGeometryNull")]
    [InlineData("", "esriGeometryNull")]
    public void GeometryType_maps_engine_names_to_esri_constants(string engineType, string expected)
    {
        Assert.Equal(expected, EsriLayerModel.GeometryType(engineType));
    }

    private static readonly FeatureSchema StringSchema = new(
    [
        new FieldDefinition("guid", AttributeKind.String),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema IntSchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription StringLayer() => new(
        "demo.devices", "demo", "devices", "geometry", 4326, "Point", 3, ["guid"], StringSchema);

    private static DatasetDescription IntLayer() => new(
        "demo.places", "demo", "places", "geometry", 4326, "Point", 5, ["id"], IntSchema);

    private static DatasetDescription LayoutLayer(CoordinateLayout layout) => new(
        "demo.survey", "demo", "survey", "geometry", 4326, "Point", 5, ["id"], IntSchema, layout);

    [Fact]
    public void Describe_advertises_the_string_unique_id_field()
    {
        var dataset = StringLayer();

        var layer = EsriLayerModel.Describe(0, dataset, editable: false);

        var scheme = EsriUniqueIdScheme.For(dataset);
        Assert.NotNull(scheme);
        Assert.NotNull(layer.UniqueIdField);
        Assert.Equal(scheme.FieldName, layer.UniqueIdField.Name);
        Assert.False(layer.UniqueIdField.IsSystemMaintained);
    }

    [Fact]
    public void Describe_omits_unique_id_field_without_a_string_identity()
    {
        var layer = EsriLayerModel.Describe(0, IntLayer(), editable: false);

        Assert.Null(EsriUniqueIdScheme.For(IntLayer()));
        Assert.Null(layer.UniqueIdField);
    }

    [Fact]
    public void Unique_id_field_serializes_in_the_esri_shape()
    {
        var layer = EsriLayerModel.Describe(0, StringLayer(), editable: false);

        var json = JsonSerializer.Serialize(layer, EsriJson.Options);
        using var document = JsonDocument.Parse(json);
        var field = document.RootElement.GetProperty("uniqueIdField");
        Assert.Equal("guid", field.GetProperty("name").GetString());
        Assert.False(field.GetProperty("isSystemMaintained").GetBoolean());
    }

    [Fact]
    public void Layers_without_a_string_identity_omit_unique_id_field_on_the_wire()
    {
        var layer = EsriLayerModel.Describe(0, IntLayer(), editable: false);

        var json = JsonSerializer.Serialize(layer, EsriJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("uniqueIdField", out _));
    }

    [Theory]
    [InlineData(CoordinateLayout.Xyz, true, false)]
    [InlineData(CoordinateLayout.Xym, false, true)]
    [InlineData(CoordinateLayout.Xyzm, true, true)]
    [InlineData(CoordinateLayout.Xy, false, false)]
    public void Describe_advertises_the_ordinates_the_dataset_declares(CoordinateLayout layout, bool hasZ, bool hasM)
    {
        var layer = EsriLayerModel.Describe(0, LayoutLayer(layout), editable: false);

        Assert.Equal(hasZ, layer.HasZ ?? false);
        Assert.Equal(hasM, layer.HasM ?? false);
    }

    [Fact]
    public void A_two_dimensional_dataset_advertises_neither_ordinate()
    {
        var layer = EsriLayerModel.Describe(0, LayoutLayer(CoordinateLayout.Xy), editable: false);

        // Absent, not `false`: the flag is a claim the dataset makes about its
        // own data, and a dataset that never declared the ordinate makes none.
        Assert.Null(layer.HasZ);
        Assert.Null(layer.HasM);
    }

    [Fact]
    public void A_three_dimensional_dataset_advertises_hasZ_and_not_hasM_on_the_wire()
    {
        var layer = EsriLayerModel.Describe(0, LayoutLayer(CoordinateLayout.Xyz), editable: false);

        var json = JsonSerializer.Serialize(layer, EsriJson.Options);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("hasZ").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("hasM", out _));
    }

    [Fact]
    public void A_table_advertises_neither_ordinate()
    {
        var table = new DatasetDescription(
            "demo.totals", "demo", "totals", string.Empty, 0, string.Empty, 2, ["id"], StringSchema);

        var layer = EsriLayerModel.Describe(0, table, editable: false, isTable: true);

        Assert.Null(layer.HasZ);
        Assert.Null(layer.HasM);
    }
}
