using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

public class GeometryConstructionTests
{
    [Fact]
    public void Point_derives_layout_from_ordinates()
    {
        Assert.Equal(CoordinateLayout.Xy, GeometryFactory.CreatePoint(1, 2).Layout);
        Assert.Equal(CoordinateLayout.Xyz, GeometryFactory.CreatePoint(1, 2, 3).Layout);
        Assert.Equal(CoordinateLayout.Xym, new Point(new Coordinate(1, 2, M: 4)).Layout);
        Assert.Equal(CoordinateLayout.Xyzm, GeometryFactory.CreatePoint(1, 2, 3, 4).Layout);
        Assert.Equal(new Coordinate(1, 2, Z: 3, M: 4), GeometryFactory.CreatePoint(1, 2, 3, 4).Coordinate);
    }

    [Fact]
    public void Point_empty_state_and_envelope()
    {
        var point = GeometryFactory.CreateEmptyPoint();
        Assert.True(point.IsEmpty);
        Assert.Null(point.Coordinate);
        Assert.Null(point.Envelope);
        Assert.Equal(0, point.CoordinateCount);

        var located = GeometryFactory.CreatePoint(3, 4);
        Assert.False(located.IsEmpty);
        Assert.Equal(1, located.CoordinateCount);
        Assert.Equal(new Envelope(3, 4, 3, 4), located.Envelope);
        Assert.Equal(3, located.X);
        Assert.Equal(4, located.Y);
    }

    [Fact]
    public void Empty_point_can_carry_an_explicit_layout()
    {
        var point = GeometryFactory.CreateEmptyPoint(layout: CoordinateLayout.Xym);
        Assert.True(point.IsEmpty);
        Assert.Equal(CoordinateLayout.Xym, point.Layout);
    }

    [Fact]
    public void Point_with_nan_coordinates_has_no_envelope()
    {
        var point = GeometryFactory.CreatePoint(double.NaN, 4);
        Assert.Null(point.Envelope);
        Assert.Equal(1, point.CoordinateCount);
    }

    [Fact]
    public void Point_with_infinite_coordinates_throws_when_envelope_is_read()
    {
        var point = GeometryFactory.CreatePoint(double.PositiveInfinity, 4);
        Assert.Throws<ArgumentException>(() => point.Envelope);
    }

    [Fact]
    public void LineString_from_coordinates_and_backing_copy()
    {
        var coordinates = new[] { new Coordinate(0, 0), new Coordinate(5, 3), new Coordinate(10, 2) };
        var line = GeometryFactory.CreateLineString(coordinates);
        coordinates[1] = new Coordinate(999, 999);

        Assert.Equal(GeometryType.LineString, line.Type);
        Assert.Equal(3, line.CoordinateCount);
        Assert.Equal(new Coordinate(5, 3), line[1]);
        Assert.Equal(new Envelope(0, 0, 10, 3), line.Envelope);

        // The factory's backing sequence is packed and independent of the input.
        Assert.IsType<PackedCoordinateSequence>(line.Sequence);
    }

    [Fact]
    public void Empty_line_string_layout_is_preserved()
    {
        var line = GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xyz);
        Assert.True(line.IsEmpty);
        Assert.Equal(0, line.CoordinateCount);
        Assert.Null(line.Envelope);
        Assert.Equal(CoordinateLayout.Xyz, line.Layout);
    }

    [Fact]
    public void Polygon_rings_and_envelope()
    {
        var exterior = GeometryFactory.CreateLineString([
            new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0),
        ]);
        var hole = GeometryFactory.CreateLineString([
            new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4),
        ]);

        var polygon = GeometryFactory.CreatePolygon(exterior, [hole]);
        Assert.Equal(GeometryType.Polygon, polygon.Type);
        Assert.Equal(10, polygon.CoordinateCount);
        Assert.Same(exterior, polygon.ExteriorRing);
        Assert.Single(polygon.InteriorRings);
        Assert.Same(hole, polygon.InteriorRings[0]);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);

        var empty = GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString());
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.CoordinateCount);
        Assert.Null(empty.Envelope);
    }

    [Fact]
    public void Polygon_interior_ring_list_is_defensively_copied()
    {
        var rings = new List<LineString> { GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]) };
        var polygon = new Polygon(GeometryFactory.CreateEmptyLineString(), rings);
        rings.Clear();
        Assert.Single(polygon.InteriorRings);
    }

    [Fact]
    public void Multi_point_merges_layouts()
    {
        var mixed = GeometryFactory.CreateMultiPoint(
            GeometryFactory.CreatePoint(1, 2),
            GeometryFactory.CreatePoint(3, 4, 5));
        Assert.Equal(CoordinateLayout.Xyz, mixed.Layout);
        Assert.Equal(2, mixed.CoordinateCount);
        Assert.False(mixed.IsEmpty); // a non-empty child keeps the composite non-empty

        var zAndM = GeometryFactory.CreateMultiPoint(
            GeometryFactory.CreatePoint(1, 2, 5),
            new Point(new Coordinate(3, 4, M: 6)));
        Assert.Equal(CoordinateLayout.Xyzm, zAndM.Layout);

        var allEmpty = GeometryFactory.CreateMultiPoint(GeometryFactory.CreateEmptyPoint(), GeometryFactory.CreateEmptyPoint());
        Assert.True(allEmpty.IsEmpty);
        Assert.Equal(0, allEmpty.CoordinateCount);
        Assert.Null(allEmpty.Envelope);
        Assert.False(GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(1, 2)).IsEmpty);
    }

    [Fact]
    public void Composite_envelopes_union_children()
    {
        var poly = GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString([
            new Coordinate(0, 0), new Coordinate(5, 0), new Coordinate(5, 5), new Coordinate(0, 5), new Coordinate(0, 0),
        ]));
        var point = GeometryFactory.CreatePoint(10, 10);

        var collection = GeometryFactory.CreateGeometryCollection(poly, point);
        Assert.Equal(6, collection.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 10, 10), collection.Envelope);
        Assert.Equal(GeometryType.GeometryCollection, collection.Type);
    }

    [Fact]
    public void Composite_crs_resolution()
    {
        var epsg = CoordinateReference.Epsg(4326);
        var other = CoordinateReference.Epsg(3857);

        // Explicit CRS wins over unspecified parts.
        Assert.Equal(epsg, GeometryFactory.CreateMultiPoint(new[] { GeometryFactory.CreateEmptyPoint() }, epsg).CoordinateReference);
        // A part CRS alone normalises the composite.
        Assert.Equal(epsg, GeometryFactory.CreateMultiPoint(new[] { GeometryFactory.CreatePoint(1, 2, epsg) }).CoordinateReference);
        // Mixed null and equal CRSs are fine.
        Assert.Equal(epsg, GeometryFactory.CreateGeometryCollection(
            new IGeometry[] { GeometryFactory.CreatePoint(1, 2, epsg), GeometryFactory.CreateEmptyPoint() }, epsg).CoordinateReference);
        // Conflicting CRSs are rejected.
        Assert.Throws<ArgumentException>(() =>
            GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(1, 2, epsg), GeometryFactory.CreatePoint(3, 4, other)));
    }

    [Fact]
    public void Collection_children_are_defensively_copied()
    {
        var points = new List<Point> { GeometryFactory.CreatePoint(1, 2) };
        var multiPoint = new MultiPoint(points);
        points.Clear();
        Assert.Single(multiPoint.Points);
    }

    [Fact]
    public void Crs_is_preserved_on_leaf_factories()
    {
        var crs = CoordinateReference.Epsg(4326);
        Assert.Equal(crs, GeometryFactory.CreatePoint(1, 2, crs).CoordinateReference);
        Assert.Equal(crs, GeometryFactory.CreateLineString([new Coordinate(1, 2)], crs).CoordinateReference);
        Assert.Equal(crs, GeometryFactory.CreateEmptyPoint(crs).CoordinateReference);
    }
}
