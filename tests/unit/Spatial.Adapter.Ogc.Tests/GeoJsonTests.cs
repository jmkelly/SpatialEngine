using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The core-typed GeoJSON writer (ADR-0052 §3): the primary geometry column
/// becomes the feature geometry, the remaining attributes become properties,
/// and every simple-feature geometry maps to its coordinate nesting.
/// </summary>
public sealed class GeoJsonTests
{
    [Fact]
    public void A_point_feature_carries_its_id_properties_and_coordinates()
    {
        var feature = OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676);

        using var document = JsonDocument.Parse(GeoJson.FeatureCollection([(OgcFixtures.Description(), feature)]));
        var root = document.RootElement;
        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
        var written = root.GetProperty("features")[0];
        Assert.Equal("amsterdam", written.GetProperty("id").GetString());
        Assert.Equal("Amsterdam", written.GetProperty("properties").GetProperty("name").GetString());
        Assert.Equal(900_000, written.GetProperty("properties").GetProperty("population").GetInt64());
        Assert.False(written.GetProperty("properties").TryGetProperty("geometry", out _));
        var coordinates = written.GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(4.9041, coordinates[0].GetDouble());
        Assert.Equal(52.3676, coordinates[1].GetDouble());
    }

    [Fact]
    public void Each_geometry_family_uses_the_documented_coordinate_nesting()
    {
        Assert.Equal("Point", Geometry(GeometryFactory.CreatePoint(1, 2)).GetProperty("type").GetString());
        Assert.Equal(
            "LineString",
            Geometry(GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])).GetProperty("type").GetString());
        Assert.Equal(
            "Polygon",
            Geometry(GeometryFactory.CreatePolygon(
                [
                    new Coordinate(0, 0),
                    new Coordinate(1, 0),
                    new Coordinate(1, 1),
                    new Coordinate(0, 0),
                ])).GetProperty("type").GetString());
        Assert.Equal(
            "MultiPoint",
            Geometry(GeometryFactory.CreateMultiPoint(
                GeometryFactory.CreatePoint(0, 0),
                GeometryFactory.CreatePoint(1, 1))).GetProperty("type").GetString());
        Assert.Equal(
            "GeometryCollection",
            Geometry(GeometryFactory.CreateGeometryCollection(
                GeometryFactory.CreatePoint(0, 0),
                GeometryFactory.CreatePoint(1, 1))).GetProperty("type").GetString());
    }

    [Fact]
    public void A_polygon_writes_an_outer_ring()
    {
        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(2, 0),
            new Coordinate(2, 2),
            new Coordinate(0, 0),
        ]);

        var coordinates = Geometry(polygon).GetProperty("coordinates");

        Assert.Equal(1, coordinates.GetArrayLength());
        Assert.Equal(4, coordinates[0].GetArrayLength());
        Assert.Equal(2, coordinates[0][1][0].GetDouble());
    }

    [Fact]
    public void A_null_geometry_is_emitted_as_null()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var feature = new Feature(
            new FeatureId("empty"),
            schema,
            [AttributeValue.FromString("empty"), AttributeValue.Null]);
        var description = new Spatial.PluginSdk.Providers.DatasetDescription(
            "demo.empty", "demo", "empty", "geometry", 4326, "Unknown", 1, ["name"], schema);

        using var document = JsonDocument.Parse(GeoJson.FeatureCollection([(description, feature)]));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("features")[0].GetProperty("geometry").ValueKind);
    }

    private static JsonElement Geometry(IGeometry geometry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            GeoJson.WriteGeometry(writer, geometry);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
