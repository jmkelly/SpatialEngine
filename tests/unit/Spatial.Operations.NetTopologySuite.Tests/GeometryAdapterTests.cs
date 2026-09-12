using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// The private core ↔ NTS adapter round trips (ADR-0005): every core geometry
/// shape converts to an NTS geometry the planar algorithms run on and back
/// without loss of structure, ordinates or CRS identity; ring closure is
/// normalised to NTS's requirement; empty geometries stay empty. The adapter
/// is internal — these tests pin its behaviour through
/// <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class GeometryAdapterTests
{
    private static readonly CoordinateReference Crs = new("EPSG", "4326");

    [Fact]
    public void Point_round_trips()
    {
        var point = GeometryFactory.CreatePoint(1.5, -2.25, Crs);
        var back = Assert.IsType<Point>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(point), Crs));
        Assert.Equal(point, back);
    }

    [Fact]
    public void Point_with_z_round_trips()
    {
        var point = GeometryFactory.CreatePoint(1.5, -2.25, 9.5, Crs);
        var back = Assert.IsType<Point>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(point), Crs));
        Assert.Equal(point, back);
        Assert.Equal(CoordinateLayout.Xyz, back.Layout);
    }

    [Fact]
    public void Point_with_z_and_m_round_trips()
    {
        var point = GeometryFactory.CreatePoint(1.5, -2.25, 9.5, 99.0, Crs);
        var back = Assert.IsType<Point>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(point), Crs));
        Assert.Equal(CoordinateLayout.Xyzm, back.Layout);
        Assert.Equal(point.Coordinate, back.Coordinate);
        Assert.Equal(Crs, back.CoordinateReference);
    }

    [Fact]
    public void Empty_point_round_trips()
    {
        var point = GeometryFactory.CreateEmptyPoint(Crs);
        var back = Assert.IsType<Point>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(point), Crs));
        Assert.True(back.IsEmpty);
        Assert.Equal(point, back);
    }

    [Fact]
    public void Line_string_round_trips()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 3), new Coordinate(2, -1)], Crs);
        var back = Assert.IsType<LineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(line), Crs));
        Assert.Equal(line, back);
    }

    [Fact]
    public void Line_string_with_z_round_trips()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0, 1), new Coordinate(1, 3, 2), new Coordinate(2, -1, 3)], Crs);
        var back = Assert.IsType<LineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(line), Crs));
        Assert.Equal(line, back);
        Assert.Equal(CoordinateLayout.Xyz, back.Layout);
    }

    [Fact]
    public void Line_string_with_z_and_m_round_trips()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0, 1, 10), new Coordinate(1, 3, 2, 20), new Coordinate(2, -1, 3, 30)], Crs);
        var back = Assert.IsType<LineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(line), Crs));
        Assert.Equal(CoordinateLayout.Xyzm, back.Layout);
        Assert.Equal(line.Sequence.Count, back.Sequence.Count);
        for (var i = 0; i < line.Sequence.Count; i++)
        {
            Assert.Equal(line.Sequence.GetCoordinate(i), back.Sequence.GetCoordinate(i));
        }
        Assert.Equal(Crs, back.CoordinateReference);
    }

    [Fact]
    public void Empty_line_string_round_trips()
    {
        var line = GeometryFactory.CreateEmptyLineString(layout: CoordinateLayout.Xyz, coordinateReference: Crs);
        var back = Assert.IsType<LineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(line), Crs));
        Assert.True(back.IsEmpty);
        Assert.Equal(line, back);
    }

    [Fact]
    public void Polygon_with_holes_round_trips()
    {
        var exterior = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]);
        var holeA = GeometryFactory.CreateLineString(
            [new Coordinate(2, 2), new Coordinate(4, 2), new Coordinate(4, 4), new Coordinate(2, 4), new Coordinate(2, 2)]);
        var holeB = GeometryFactory.CreateLineString(
            [new Coordinate(6, 6), new Coordinate(8, 6), new Coordinate(8, 8), new Coordinate(6, 8), new Coordinate(6, 6)]);
        var polygon = GeometryFactory.CreatePolygon(exterior, [holeA, holeB], Crs);

        var back = Assert.IsType<Polygon>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(polygon), Crs));
        Assert.Equal(polygon, back);
        Assert.Equal(2, back.InteriorRings.Count);
    }

    [Fact]
    public void An_open_ring_is_closed_for_nets_topology_suite()
    {
        // The core model does not require closed rings; NTS LinearRings do.
        // The adapter appends the first coordinate as the closing coordinate.
        var open = GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10)]);
        var converted = GeometryAdapter.ToNts(open);
        var back = Assert.IsType<Polygon>(GeometryAdapter.ToCore(converted, null));
        Assert.Equal(4, back.ExteriorRing.CoordinateCount);
        Assert.Equal(back.ExteriorRing.Sequence.GetCoordinate(0), back.ExteriorRing.Sequence.GetCoordinate(3));
    }

    [Fact]
    public void Empty_polygon_round_trips()
    {
        var polygon = GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString(coordinateReference: Crs), coordinateReference: Crs);
        var back = Assert.IsType<Polygon>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(polygon), Crs));
        Assert.True(back.IsEmpty);
    }

    [Fact]
    public void Multi_geometries_round_trip()
    {
        var multiPoint = GeometryFactory.CreateMultiPoint(
            [GeometryFactory.CreatePoint(0, 0), GeometryFactory.CreatePoint(1, 1)], Crs);
        var multiLine = GeometryFactory.CreateMultiLineString(
            [
                GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]),
                GeometryFactory.CreateLineString([new Coordinate(2, 2), new Coordinate(3, 3)]),
            ],
            Crs);
        var multiPolygon = GeometryFactory.CreateMultiPolygon(
            [GeometryFactory.CreatePolygon([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(0, 0)])],
            Crs);

        Assert.Equal(multiPoint, Assert.IsType<MultiPoint>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(multiPoint), Crs)));
        Assert.Equal(multiLine, Assert.IsType<MultiLineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(multiLine), Crs)));
        Assert.Equal(multiPolygon, Assert.IsType<MultiPolygon>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(multiPolygon), Crs)));
    }

    [Fact]
    public void Geometry_collection_round_trips()
    {
        var collection = GeometryFactory.CreateGeometryCollection(
            [
                GeometryFactory.CreatePoint(0, 0),
                GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]),
                GeometryFactory.CreatePolygon([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(0, 0)]),
            ],
            Crs);

        var back = Assert.IsType<GeometryCollection>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(collection), Crs));
        Assert.Equal(3, back.Geometries.Count);
        Assert.Equal(collection, back);
    }

    [Fact]
    public void An_empty_collection_round_trips_as_empty()
    {
        var collection = GeometryFactory.CreateGeometryCollection([], Crs);
        var back = Assert.IsType<GeometryCollection>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(collection), Crs));
        Assert.True(back.IsEmpty);
    }

    [Fact]
    public void Crs_identity_is_preserved()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Crs);
        var back = Assert.IsType<LineString>(GeometryAdapter.ToCore(GeometryAdapter.ToNts(line), Crs));
        Assert.Equal(Crs, back.CoordinateReference);
    }

    [Fact]
    public void Unknown_core_geometry_type_throws()
    {
        var unknown = new UnknownGeometry();
        var exception = Assert.Throws<ArgumentException>(() => GeometryAdapter.ToNts(unknown));
        Assert.Contains("Cannot convert core geometry type", exception.Message);
    }

    private sealed class UnknownGeometry : IGeometry
    {
        public GeometryType Type => unchecked((GeometryType)255);
        public CoordinateLayout Layout => CoordinateLayout.Xy;
        public CoordinateReference? CoordinateReference => null;
        public bool IsEmpty => true;
        public int CoordinateCount => 0;
        public Envelope? Envelope => null;
    }
}
