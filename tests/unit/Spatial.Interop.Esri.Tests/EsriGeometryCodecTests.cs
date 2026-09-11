using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Esri.Tests;

public sealed class EsriGeometryCodecTests
{
    private static IGeometry Decode(string json) => EsriGeometryCodec.Decode(JsonDocument.Parse(json).RootElement);

    private static IGeometry RoundTrip(IGeometry geometry) => Decode(EsriGeometryCodec.Encode(geometry));

    [Fact]
    public void Point_round_trips_with_its_spatial_reference()
    {
        var decoded = Assert.IsAssignableFrom<Point>(Decode(
            """{"x":-118.15,"y":33.8,"spatialReference":{"wkid":4326}}"""));

        Assert.Equal(-118.15, decoded.X);
        Assert.Equal(33.8, decoded.Y);
        Assert.Equal(CoordinateReference.Epsg(4326), decoded.CoordinateReference);
        Assert.True(GeometryComparer.Equals(decoded, RoundTrip(decoded)));
    }

    [Fact]
    public void Point_carries_optional_z_and_m()
    {
        var decoded = Assert.IsAssignableFrom<Point>(Decode("""{"x":1,"y":2,"z":3,"m":4,"hasZ":true,"hasM":true}"""));

        Assert.Equal(3, decoded.Z);
        Assert.Equal(4, decoded.M);
        Assert.Equal(CoordinateLayout.Xyzm, decoded.Layout);
    }

    [Fact]
    public void Multipoint_round_trips()
    {
        var decoded = Assert.IsAssignableFrom<MultiPoint>(Decode("""{"points":[[1,2],[3,4]],"spatialReference":{"wkid":3857}}"""));

        Assert.Equal(2, decoded.Points.Count);
        Assert.Equal(CoordinateReference.Epsg(3857), decoded.CoordinateReference);
        Assert.True(GeometryComparer.Equals(decoded, RoundTrip(decoded)));
    }

    [Fact]
    public void A_single_path_polyline_decodes_to_a_line_string()
    {
        var decoded = Decode("""{"paths":[[[0,0],[1,1],[2,2]]]}""");

        var line = Assert.IsAssignableFrom<LineString>(decoded);
        Assert.Equal(3, line.CoordinateCount);
        Assert.True(GeometryComparer.Equals(decoded, RoundTrip(decoded)));
    }

    [Fact]
    public void A_multi_path_polyline_decodes_to_a_multi_line_string()
    {
        var decoded = Decode("""{"paths":[[[0,0],[1,1]],[[2,2],[3,3]]]}""");

        var multi = Assert.IsAssignableFrom<MultiLineString>(decoded);
        Assert.Equal(2, multi.LineStrings.Count);
        Assert.True(GeometryComparer.Equals(decoded, RoundTrip(decoded)));
    }

    [Fact]
    public void Polygon_rings_split_into_exterior_and_holes_by_orientation()
    {
        var decoded = Decode(
            """{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]],[[0.2,0.2],[0.8,0.2],[0.8,0.8],[0.2,0.8],[0.2,0.2]]]}""");

        var polygon = Assert.IsAssignableFrom<Polygon>(decoded);
        Assert.Single(polygon.InteriorRings);
        Assert.True(GeometryComparer.Equals(decoded, RoundTrip(decoded)));
    }

    [Fact]
    public void Multiple_clockwise_outer_rings_decode_to_a_multi_polygon()
    {
        var decoded = Decode(
            """{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]],[[2,2],[2,3],[3,3],[3,2],[2,2]]]}""");

        var multi = Assert.IsAssignableFrom<MultiPolygon>(decoded);
        Assert.Equal(2, multi.Polygons.Count);
    }

    [Fact]
    public void Envelope_decodes_to_a_closed_rectangle()
    {
        var decoded = Decode("""{"xmin":0,"ymin":0,"xmax":2,"ymax":1}""");

        var polygon = Assert.IsAssignableFrom<Polygon>(decoded);
        Assert.Equal(5, polygon.ExteriorRing.CoordinateCount);
        Assert.Equal(0, polygon.Envelope!.Value.MinX);
        Assert.Equal(2, polygon.Envelope!.Value.MaxX);
    }

    [Fact]
    public void An_inverted_envelope_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() => Decode("""{"xmin":5,"ymin":0,"xmax":1,"ymax":1}"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public void The_url_remote_geometry_form_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() => Decode("""{"url":"https://example.com/geometry.json"}"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("url", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unrecognised_shape_is_rejected()
    {
        Assert.Throws<EsriInteropException>(() => Decode("""{"unexpected":true}"""));
    }

    [Fact]
    public void A_fallback_spatial_reference_applies_when_the_geometry_carries_none()
    {
        var decoded = EsriGeometryCodec.Decode(JsonDocument.Parse("""{"x":1,"y":2}""").RootElement, CoordinateReference.Epsg(27700));

        Assert.Equal(CoordinateReference.Epsg(27700), decoded.CoordinateReference);
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData(" 1.5 , -2.5 ")]
    public void Simple_point_syntax_parses(string text)
    {
        Assert.True(EsriGeometryCodec.TryParseSimple(text, out var geometry));
        Assert.IsAssignableFrom<Point>(geometry);
    }

    [Fact]
    public void Simple_envelope_syntax_parses()
    {
        Assert.True(EsriGeometryCodec.TryParseSimple("0,0,4,2", out var geometry));

        var polygon = Assert.IsAssignableFrom<Polygon>(geometry);
        Assert.Equal(4, polygon.Envelope!.Value.MaxX);
        Assert.Equal(2, polygon.Envelope!.Value.MaxY);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("a,b")]
    [InlineData("1,2,3")]
    public void Malformed_simple_syntax_is_rejected(string text)
    {
        Assert.False(EsriGeometryCodec.TryParseSimple(text, out _));
    }
}
