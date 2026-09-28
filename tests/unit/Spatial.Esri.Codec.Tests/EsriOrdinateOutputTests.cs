using System.Text;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The <c>returnZ</c>/<c>returnM</c> output selection (spec §10, §9.1.4)
/// and the <c>hasZ</c>/<c>hasM</c> flags that make an Esri coordinate
/// array's third/fourth number unambiguous. A 3-ordinate array is Z when
/// only <c>hasZ</c> is set and M when only <c>hasM</c> is set, so the
/// writer has to state the flags; the engine's canonical binary already
/// round-trips both, so nothing is dropped by the projection itself.
/// </summary>
public sealed class EsriOrdinateOutputTests
{
    [Fact]
    public void An_xyz_point_writes_the_hasZ_flag()
    {
        var root = Write(GeometryFactory.CreatePoint(1, 2, 3), EsriOrdinateOutput.All);

        Assert.Equal(3, root.GetProperty("z").GetDouble());
        Assert.True(root.GetProperty("hasZ").GetBoolean());
        Assert.False(root.TryGetProperty("hasM", out _));
    }

    [Fact]
    public void An_xym_point_writes_the_hasM_flag()
    {
        var root = Write(GeometryFactory.CreatePoint(new Coordinate(1, 2, M: 4)), EsriOrdinateOutput.All);

        Assert.Equal(4, root.GetProperty("m").GetDouble());
        Assert.True(root.GetProperty("hasM").GetBoolean());
        Assert.False(root.TryGetProperty("hasZ", out _));
    }

    [Fact]
    public void An_xy_point_writes_neither_flag()
    {
        var root = Write(GeometryFactory.CreatePoint(1, 2), EsriOrdinateOutput.All);

        Assert.False(root.TryGetProperty("hasZ", out _));
        Assert.False(root.TryGetProperty("hasM", out _));
    }

    [Fact]
    public void ReturnZ_false_drops_z_from_a_point()
    {
        var root = Write(GeometryFactory.CreatePoint(1, 2, 3, 4), new EsriOrdinateOutput(ReturnZ: false, ReturnM: true));

        Assert.False(root.TryGetProperty("z", out _));
        Assert.Equal(4, root.GetProperty("m").GetDouble());
        Assert.False(root.TryGetProperty("hasZ", out _));
        Assert.True(root.GetProperty("hasM").GetBoolean());
    }

    [Fact]
    public void An_xyzm_polyline_writes_four_ordinate_arrays_and_both_flags()
    {
        var root = Write(Line([new Coordinate(0, 0, Z: 1, M: 2), new Coordinate(5, 5, Z: 3, M: 4)]), EsriOrdinateOutput.All);

        var path = root.GetProperty("paths")[0];
        Assert.Equal(4, path[0].GetArrayLength());
        Assert.True(root.GetProperty("hasZ").GetBoolean());
        Assert.True(root.GetProperty("hasM").GetBoolean());
    }

    [Fact]
    public void ReturnZ_false_turns_an_xyzm_array_into_an_xym_one()
    {
        var root = Write(Line([new Coordinate(0, 0, Z: 1, M: 2)]), new EsriOrdinateOutput(ReturnZ: false, ReturnM: true));

        var coordinate = root.GetProperty("paths")[0][0];
        Assert.Equal(3, coordinate.GetArrayLength());
        Assert.Equal(2, coordinate[2].GetDouble());
        Assert.False(root.TryGetProperty("hasZ", out _));
        Assert.True(root.GetProperty("hasM").GetBoolean());
    }

    [Fact]
    public void ReturnZ_and_returnM_false_write_two_ordinate_arrays()
    {
        var root = Write(Line([new Coordinate(0, 0, Z: 1, M: 2)]), EsriOrdinateOutput.Xy);

        var coordinate = root.GetProperty("paths")[0][0];
        Assert.Equal(2, coordinate.GetArrayLength());
        Assert.False(root.TryGetProperty("hasZ", out _));
        Assert.False(root.TryGetProperty("hasM", out _));
    }

    [Fact]
    public void A_polygon_honours_the_selection_on_every_ring()
    {
        var polygon = GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString(
                [new Coordinate(0, 0, Z: 1, M: 2), new Coordinate(10, 0, Z: 1, M: 2), new Coordinate(10, 10, Z: 1, M: 2), new Coordinate(0, 0, Z: 1, M: 2)],
                CoordinateLayout.Xyzm),
            [GeometryFactory.CreateLineString(
                [new Coordinate(4, 4, Z: 1, M: 2), new Coordinate(6, 4, Z: 1, M: 2), new Coordinate(4, 4, Z: 1, M: 2)],
                CoordinateLayout.Xyzm)]);

        var root = Write(polygon, EsriOrdinateOutput.Xy);

        Assert.Equal(2, root.GetProperty("rings")[0][0].GetArrayLength());
        Assert.Equal(2, root.GetProperty("rings")[1][0].GetArrayLength());
    }

    [Fact]
    public void A_multipolygon_flag_covers_the_most_elevated_part()
    {
        var multi = GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0)])),
            GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString(
                [new Coordinate(5, 5, Z: 9), new Coordinate(6, 5, Z: 9), new Coordinate(6, 6, Z: 9), new Coordinate(5, 5, Z: 9)],
                CoordinateLayout.Xyz)));

        var root = Write(multi, EsriOrdinateOutput.All);

        Assert.True(root.GetProperty("hasZ").GetBoolean());
        Assert.Equal(2, root.GetProperty("rings")[0][0].GetArrayLength());
        Assert.Equal(3, root.GetProperty("rings")[1][0].GetArrayLength());
    }

    [Fact]
    public void An_empty_geometry_writes_its_shape_without_coordinates()
    {
        var root = Write(GeometryFactory.CreateEmptyPoint(layout: CoordinateLayout.Xyzm), EsriOrdinateOutput.All);

        Assert.False(root.TryGetProperty("x", out _));
        Assert.True(root.GetProperty("hasZ").GetBoolean());
    }

    [Fact]
    public void A_feature_write_honours_the_selection_for_its_geometry()
    {
        var element = WriteFeature(new EsriFeatureWriteOptions("OBJECTID", 7, ReturnZ: false, ReturnM: false));
        var geometry = element.GetProperty("geometry");

        Assert.Equal(2, geometry.GetProperty("paths")[0][0].GetArrayLength());
        Assert.False(geometry.TryGetProperty("hasZ", out _));
    }

    [Fact]
    public void A_feature_write_can_add_a_centroid_beside_its_geometry()
    {
        var element = WriteFeature(new EsriFeatureWriteOptions(
            "OBJECTID", 7, Centroid: geometry => GeometryFactory.CreatePoint(4.9, 52.4, geometry.CoordinateReference)));

        Assert.Equal(4.9, element.GetProperty("centroid").GetProperty("x").GetDouble());
        Assert.Equal(4326, element.GetProperty("centroid").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void A_feature_write_without_a_centroid_resolver_omits_the_property()
    {
        var element = WriteFeature(new EsriFeatureWriteOptions("OBJECTID", 7));

        Assert.False(element.TryGetProperty("centroid", out _));
    }

    [Fact]
    public void A_centroid_of_a_feature_without_geometry_is_null()
    {
        var element = WriteFeature(
            new EsriFeatureWriteOptions("OBJECTID", 7, Centroid: geometry => GeometryFactory.CreatePoint(1, 1)),
            geometry: AttributeValue.Null);

        Assert.Equal(JsonValueKind.Null, element.GetProperty("centroid").ValueKind);
    }

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true)]);

    private static LineString Line(Coordinate[] coordinates) =>
        GeometryFactory.CreateLineString(coordinates, CoordinateLayout.Xyzm, CoordinateReference.Epsg(4326));

    private static JsonElement Write(IGeometry geometry, EsriOrdinateOutput output) =>
        Parse(EsriGeometryCodec.Encode(geometry, output));

    private static JsonElement WriteFeature(EsriFeatureWriteOptions options, AttributeValue? geometry = null)
    {
        var feature = new Feature(
            new FeatureId("feature"),
            Schema,
            [geometry ?? AttributeValue.FromGeometry(Line([new Coordinate(0, 0, Z: 1, M: 2)]))]);
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("feature");
            EsriFeatureCodec.Write(writer, feature, options);
            writer.WriteEndObject();
        }

        return Parse(Encoding.UTF8.GetString(stream.ToArray())).GetProperty("feature");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
