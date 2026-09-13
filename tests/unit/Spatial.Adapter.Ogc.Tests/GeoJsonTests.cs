using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The core-typed GeoJSON writer (ADR-0053 §3): the primary geometry column
/// becomes the feature geometry, the remaining attributes become properties,
/// and every simple-feature geometry maps to its coordinate nesting. Every
/// attribute kind and geometry family is exercised so the dispatch tables are
/// fully covered.
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
    public void Attribute_values_map_to_json_values()
    {
        Assert.Equal(JsonValueKind.True, Value(AttributeValue.FromBoolean(true)).ValueKind);
        Assert.Equal(JsonValueKind.False, Value(AttributeValue.FromBoolean(false)).ValueKind);
        Assert.Equal(42, Value(AttributeValue.FromInt64(42)).GetInt64());
        Assert.Equal(1.5, Value(AttributeValue.FromDouble(1.5)).GetDouble());
        Assert.Equal(JsonValueKind.Null, Value(AttributeValue.FromDouble(double.NaN)).ValueKind);
        Assert.Equal(JsonValueKind.Null, Value(AttributeValue.Null).ValueKind);
        Assert.Equal("text", Value(AttributeValue.FromString("text")).GetString());
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(guid.ToString(), Value(AttributeValue.FromGuid(guid)).GetString());
        Assert.Equal(
            "2026-09-17T00:00:00+00:00",
            Value(AttributeValue.FromDateTimeOffset(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero))).GetString());
        var geometry = Value(AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)));
        Assert.Equal("Point", geometry.GetProperty("type").GetString());
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
    public void Multi_line_strings_and_multi_polygons_nest_their_parts()
    {
        var multiLine = GeometryFactory.CreateMultiLineString(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]),
            GeometryFactory.CreateLineString([new Coordinate(2, 2), new Coordinate(3, 3)]));
        var multiPolygon = GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(
                [
                    new Coordinate(0, 0),
                    new Coordinate(1, 0),
                    new Coordinate(1, 1),
                    new Coordinate(0, 0),
                ]));

        var line = Geometry(multiLine);
        Assert.Equal("MultiLineString", line.GetProperty("type").GetString());
        Assert.Equal(2, line.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(2, line.GetProperty("coordinates")[0].GetArrayLength());

        var polygon = Geometry(multiPolygon);
        Assert.Equal("MultiPolygon", polygon.GetProperty("type").GetString());
        Assert.Equal(1, polygon.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(1, polygon.GetProperty("coordinates")[0].GetArrayLength());
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
    public void Positions_include_z_when_present_and_null_when_absent()
    {
        var withZ = GeometryFactory.CreatePoint(1, 2, 3);
        var empty = GeometryFactory.CreateEmptyPoint();

        Assert.Equal("Point", Geometry(withZ).GetProperty("type").GetString());
        Assert.Equal(3, Geometry(withZ).GetProperty("coordinates").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, Geometry(empty).GetProperty("coordinates").ValueKind);
    }

    [Fact]
    public void An_unknown_geometry_type_writes_a_null_point()
    {
        var geometry = Geometry(new StubGeometry());

        Assert.Equal("Point", geometry.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, geometry.GetProperty("coordinates").ValueKind);
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
        var description = new DatasetDescription(
            "demo.empty", "demo", "empty", "geometry", 4326, "Unknown", 1, ["name"], schema);

        using var document = JsonDocument.Parse(GeoJson.FeatureCollection([(description, feature)]));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("features")[0].GetProperty("geometry").ValueKind);
    }

    [Fact]
    public void A_feature_without_the_declared_geometry_column_writes_a_null_geometry()
    {
        var feature = OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676);
        var description = new DatasetDescription(
            "demo.cities", "demo", "cities", "shape", 4326, "Unknown", 1, ["name"], feature.Schema);

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

    private static JsonElement Value(AttributeValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            GeoJson.WriteValue(writer, value);
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>A geometry whose type is outside the core enum, to exercise the fallback writer.</summary>
    private sealed class StubGeometry : IGeometry
    {
        public GeometryType Type => (GeometryType)200;

        public CoordinateLayout Layout => CoordinateLayout.Xy;

        public CoordinateReference? CoordinateReference => null;

        public bool IsEmpty => true;

        public int CoordinateCount => 0;

        public Envelope? Envelope => null;
    }
}
