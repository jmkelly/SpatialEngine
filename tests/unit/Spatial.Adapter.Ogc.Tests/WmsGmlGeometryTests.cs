using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// Quality-loop pass: pin <c>WmsService.WriteGmlGeometry</c> (CRAP 19.1,
/// cx 9) — a pure type-switch over every geometry kind, so tests are the
/// cheap lever.
/// </summary>
public sealed class WmsGmlGeometryTests
{
    [Fact]
    public void Point_writes_a_pos()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreatePoint(1, 2));
        Assert.Equal("Point", element.Name.LocalName);
        Assert.Equal("1 2", element.Element(OgcXml.Gml + "pos")?.Value);
    }

    [Fact]
    public void Empty_point_writes_no_pos()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateEmptyPoint());
        Assert.Equal("Point", element.Name.LocalName);
        Assert.Null(element.Element(OgcXml.Gml + "pos"));
    }

    [Fact]
    public void MultiPoint_writes_point_members()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateMultiPoint(
            GeometryFactory.CreatePoint(1, 2), GeometryFactory.CreatePoint(3, 4)));
        Assert.Equal("MultiPoint", element.Name.LocalName);
        Assert.Equal(2, element.Elements(OgcXml.Gml + "pointMember").Count());
    }

    [Fact]
    public void LineString_writes_a_pos_list()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(3, 4)]));
        Assert.Equal("LineString", element.Name.LocalName);
        Assert.Equal("0 0 3 4", element.Element(OgcXml.Gml + "posList")?.Value);
    }

    [Fact]
    public void MultiLineString_writes_curve_members()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateMultiLineString(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]),
            GeometryFactory.CreateLineString([new Coordinate(2, 2), new Coordinate(3, 3)])));
        Assert.Equal("MultiCurve", element.Name.LocalName);
        Assert.Equal(2, element.Elements(OgcXml.Gml + "curveMember").Count());
    }

    [Fact]
    public void Polygon_writes_exterior_and_interior_rings()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreatePolygon(
            Ring(0, 0, 4, 4),
            [Ring(1, 1, 2, 2)]));
        Assert.Equal("Polygon", element.Name.LocalName);
        Assert.NotNull(element.Element(OgcXml.Gml + "exterior"));
        Assert.NotNull(element.Element(OgcXml.Gml + "interior"));
    }

    [Fact]
    public void MultiPolygon_writes_surface_members()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(Ring(0, 0, 1, 1)),
            GeometryFactory.CreatePolygon(Ring(2, 2, 3, 3))));
        Assert.Equal("MultiSurface", element.Name.LocalName);
        Assert.Equal(2, element.Elements(OgcXml.Gml + "surfaceMember").Count());
    }

    [Fact]
    public void GeometryCollection_writes_geometry_members()
    {
        var element = WmsGmlWriter.WriteGmlGeometry(GeometryFactory.CreateGeometryCollection(
            GeometryFactory.CreatePoint(1, 2),
            GeometryFactory.CreatePoint(3, 4)));
        Assert.Equal("MultiGeometry", element.Name.LocalName);
        Assert.Equal(2, element.Elements(OgcXml.Gml + "geometryMember").Count());
    }

    private static LineString Ring(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreateLineString(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);
}
