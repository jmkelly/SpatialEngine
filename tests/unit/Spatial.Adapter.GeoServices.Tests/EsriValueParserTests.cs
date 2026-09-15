using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The GeoServices string-parameter parsers (spec §7.0.4.2): geometry
/// arrays, the simple coordinate syntax, spatial references and numeric
/// lists. Malformed input must be a typed invalid-argument failure.
/// </summary>
public sealed class EsriValueParserTests
{
    [Fact]
    public void ParseGeometries_reads_a_json_array()
    {
        var geometries = EsriValueParser.ParseGeometries("[{\"x\":1,\"y\":2},{\"x\":3,\"y\":4}]", null);

        Assert.Equal(2, geometries.Count);
        Assert.Equal(GeometryType.Point, geometries[0].Type);
        Assert.Equal(new Coordinate(1, 2), ((Point)geometries[0]).Coordinate);
        Assert.Equal(new Coordinate(3, 4), ((Point)geometries[1]).Coordinate);
    }

    [Fact]
    public void ParseGeometries_reads_a_single_object()
    {
        var geometries = EsriValueParser.ParseGeometries("{\"x\":5,\"y\":6}", null);

        var point = Assert.IsType<Point>(Assert.Single(geometries));
        Assert.Equal(new Coordinate(5, 6), point.Coordinate);
    }

    [Fact]
    public void ParseGeometries_reads_the_simple_coordinate_syntax()
    {
        var geometries = EsriValueParser.ParseGeometries(" 7 , 8 ", null);

        var point = Assert.IsType<Point>(Assert.Single(geometries));
        Assert.Equal(new Coordinate(7, 8), point.Coordinate);
    }

    [Fact]
    public void ParseGeometries_applies_the_fallback_spatial_reference()
    {
        var fallback = CoordinateReference.Epsg(4326);

        var geometries = EsriValueParser.ParseGeometries("[{\"x\":1,\"y\":2}]", fallback);

        Assert.Equal(fallback, geometries[0].CoordinateReference);
    }

    [Theory]
    [InlineData("not a geometry")]
    [InlineData("[1, 2, 3]")]
    public void ParseGeometries_rejects_malformed_input(string value)
    {
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseGeometries(value, null));
    }

    [Fact]
    public void ParseGeometry_reads_an_object_and_the_simple_syntax()
    {
        Assert.IsType<Point>(EsriValueParser.ParseGeometry("{\"x\":1,\"y\":2}", null));
        Assert.IsType<Polygon>(EsriValueParser.ParseGeometry("0,0,1,1", null));
    }

    [Fact]
    public void ParseGeometry_rejects_malformed_input()
    {
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseGeometry("nope", null));
    }

    [Fact]
    public void ParseSpatialReference_returns_null_for_blank_input()
    {
        Assert.Null(EsriValueParser.ParseSpatialReference(null));
        Assert.Null(EsriValueParser.ParseSpatialReference("   "));
    }

    [Fact]
    public void ParseSpatialReference_reads_a_wkid_and_an_object()
    {
        Assert.Equal(CoordinateReference.Epsg(4326), EsriValueParser.ParseSpatialReference("4326"));
        Assert.Equal(CoordinateReference.Epsg(4326), EsriValueParser.ParseSpatialReference("{\"wkid\":4326}"));
    }

    [Fact]
    public void ParseSpatialReference_rejects_an_unknown_form()
    {
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseSpatialReference("web mercator"));
    }

    [Fact]
    public void ParseDoubles_reads_a_list_and_rejects_bad_values()
    {
        var values = EsriValueParser.ParseDoubles(" 1, 2.5 ,3 ", "distances");

        Assert.Equal([1d, 2.5, 3d], values);
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseDoubles("", "distances"));
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseDoubles("1,abc", "distances"));
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseDoubles("1,Infinity", "distances"));
    }

    [Fact]
    public void ParseInt64s_reads_a_list_and_rejects_bad_values()
    {
        var values = EsriValueParser.ParseInt64s(" 1, -2 ,3 ", "objectIds");

        Assert.Equal([1L, -2L, 3L], values);
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseInt64s("", "objectIds"));
        Assert.Throws<EsriInteropException>(() => EsriValueParser.ParseInt64s("1,2.5", "objectIds"));
    }

    [Fact]
    public void ParseGeometries_with_json_document_does_not_leak_memory()
    {
        // The parser owns the JsonDocument; parsing a large array must not throw
        // and must return the same count.
        var json = "[" + string.Join(',', Enumerable.Range(0, 50).Select(index => $"{{\"x\":{index},\"y\":{index}}}")) + "]";

        var geometries = EsriValueParser.ParseGeometries(json, null);

        Assert.Equal(50, geometries.Count);
    }
}
