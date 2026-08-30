using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

public class GeometryTraversalTests
{
    private static LineString Ring(params (double X, double Y)[] points) =>
        GeometryFactory.CreateLineString(points.Select(p => new Coordinate(p.X, p.Y)).ToArray());

    [Fact]
    public void Leaf_parts_are_empty()
    {
        Assert.Empty(GeometryFactory.CreatePoint(1, 2).Parts());
        Assert.Empty(GeometryFactory.CreateLineString([new Coordinate(1, 2)]).Parts());
    }

    [Fact]
    public void Polygon_parts_are_rings_in_order()
    {
        var exterior = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));
        var hole = Ring((4, 4), (6, 4), (6, 6), (4, 6), (4, 4));
        var polygon = GeometryFactory.CreatePolygon(exterior, [hole]);
        Assert.Equal(new IGeometry[] { exterior, hole }, polygon.Parts());
    }

    [Fact]
    public void Composite_parts_are_children()
    {
        var point = GeometryFactory.CreatePoint(1, 2);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2)]);
        var collection = GeometryFactory.CreateGeometryCollection(point, line);
        Assert.Equal(new IGeometry[] { point, line }, collection.Parts());
    }

    [Fact]
    public void DepthFirst_yields_self_then_descendants()
    {
        var point = GeometryFactory.CreatePoint(1, 2);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var collection = GeometryFactory.CreateGeometryCollection(point, line);
        var nested = GeometryFactory.CreateGeometryCollection(collection, GeometryFactory.CreateEmptyPoint());

        var types = nested.DepthFirst().Select(g => g.Type).ToArray();
        Assert.Equal(
            [GeometryType.GeometryCollection, GeometryType.GeometryCollection, GeometryType.Point, GeometryType.LineString, GeometryType.Point],
            types);
    }

    [Fact]
    public void Coordinates_walk_leaves_in_structural_order()
    {
        var exterior = Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));
        var hole = Ring((4, 4), (6, 4), (6, 6), (4, 6), (4, 4));
        var polygon = GeometryFactory.CreatePolygon(exterior, [hole]);

        var coordinates = polygon.Coordinates().ToArray();
        Assert.Equal(10, coordinates.Length);
        Assert.Equal(new Coordinate(0, 0), coordinates[0]);
        Assert.Equal(new Coordinate(0, 0), coordinates[4]); // exterior ring closing vertex
        Assert.Equal(new Coordinate(4, 4), coordinates[5]); // first hole vertex
        Assert.Equal(new Coordinate(4, 4), coordinates[^1]); // hole closing vertex
    }

    [Fact]
    public void Coordinates_of_collections_are_concatenated()
    {
        var point = GeometryFactory.CreatePoint(7, 8);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var nested = GeometryFactory.CreateGeometryCollection(point, line);

        Assert.Equal([new Coordinate(7, 8), new Coordinate(1, 2), new Coordinate(3, 4)], nested.Coordinates());
    }

    [Fact]
    public void Empty_geometries_yield_no_coordinates()
    {
        Assert.Empty(GeometryFactory.CreateEmptyPoint().Coordinates());
        Assert.Empty(GeometryFactory.CreateEmptyLineString().Coordinates());
        Assert.Empty(GeometryFactory.CreateGeometryCollection(GeometryFactory.CreateEmptyPoint()).Coordinates());
    }
}

public class GeometryEqualityTests
{
    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y);

    [Fact]
    public void Lines_with_different_backings_are_equal()
    {
        var packed = GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var array = new LineString(new ArrayCoordinateSequence([new Coordinate(1, 2), new Coordinate(3, 4)]));
        Assert.Equal(packed, array);
        Assert.Equal(packed.GetHashCode(), array.GetHashCode());
    }

    [Fact]
    public void Crs_participates_in_equality()
    {
        var a = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));
        var b = GeometryFactory.CreatePoint(1, 2);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Layout_participates_in_point_equality()
    {
        var a = new Point(new Coordinate(1, 2, Z: double.NaN));
        var b = GeometryFactory.CreatePoint(1, 2);
        Assert.NotEqual(a, b); // Xyz with NaN Z vs Xy
    }

    [Fact]
    public void Polygon_equality_includes_ring_order()
    {
        var ring1 = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(0, 1), new Coordinate(0, 0)]);
        var ring2 = GeometryFactory.CreateLineString([new Coordinate(0.2, 0.2), new Coordinate(0.8, 0.2), new Coordinate(0.2, 0.8), new Coordinate(0.2, 0.2)]);
        var polygon = GeometryFactory.CreatePolygon(ring1, [ring2]);

        Assert.Equal(polygon, GeometryFactory.CreatePolygon(ring1, [ring2]));
        Assert.NotEqual(polygon, GeometryFactory.CreatePolygon(ring2, [ring1]));
    }

    [Fact]
    public void Composite_order_matters()
    {
        var multiPoint = GeometryFactory.CreateMultiPoint(Point(1, 2), Point(3, 4));
        Assert.NotEqual(multiPoint, GeometryFactory.CreateMultiPoint(Point(3, 4), Point(1, 2)));
    }

    [Fact]
    public void GeometryComparer_dispatch_and_null_handling()
    {
        var point = Point(1, 2);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2)]);
        Assert.True(GeometryComparer.Equals(point, point));
        Assert.False(GeometryComparer.Equals(point, line));
        Assert.False(GeometryComparer.Equals(point, null));
        Assert.False(GeometryComparer.Equals(null, point));
        Assert.True(GeometryComparer.Equals(null, null));
        Assert.Equal(point.GetHashCode(), GeometryComparer.GetHashCode(point));
    }

    [Fact]
    public void GeometryCollection_uses_comparer_for_heterogeneous_children()
    {
        var point = Point(1, 2);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2)]);
        var collection = GeometryFactory.CreateGeometryCollection(point, line);
        var twin = GeometryFactory.CreateGeometryCollection(Point(1, 2), GeometryFactory.CreateLineString([new Coordinate(1, 2)]));
        var other = GeometryFactory.CreateGeometryCollection(line, point);

        Assert.Equal(collection, twin);
        Assert.Equal(collection.GetHashCode(), twin.GetHashCode());
        Assert.NotEqual(collection, other);
    }

    [Fact]
    public void Instance_works_in_linq()
    {
        var point = Point(1, 2);
        var duplicate = Point(1, 2);
        var distinct = new[] { point, duplicate }.Distinct(GeometryComparer.Instance).ToArray();
        Assert.Single(distinct);
        Assert.Equal(point, distinct[0]);
    }

    [Fact]
    public void Unknown_geometry_type_throws_in_comparer()
    {
        Assert.False(GeometryComparer.Equals(new ForeignGeometry(), new ForeignGeometry()));
        Assert.Throws<ArgumentException>(() => GeometryComparer.GetHashCode(new ForeignGeometry()));
    }

    private sealed class ForeignGeometry : IGeometry
    {
        public GeometryType Type => (GeometryType)200;
        public CoordinateLayout Layout => CoordinateLayout.Xy;
        public CoordinateReference? CoordinateReference => null;
        public bool IsEmpty => true;
        public int CoordinateCount => 0;
        public Envelope? Envelope => null;
    }
}
