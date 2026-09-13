using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Value-equality pins for the core geometry types (T-007). Every
/// <c>ContentEquals</c> is a conjunction of CRS identity and content identity,
/// so each type needs both directions: same CRS with differing content is
/// unequal (kills the <c>&amp;&amp;</c> → <c>||</c> mutant) and same content
/// with differing CRS is unequal (kills the ignores-CRS mutant). Equality is
/// load-bearing for dedupe and codec round-trips, so the comparer and hash
/// codes are pinned alongside.
/// </summary>
public class GeometryContentEqualsTests
{
    private static readonly CoordinateReference Epsg4326 = CoordinateReference.Epsg(4326);
    private static readonly CoordinateReference Epsg3857 = CoordinateReference.Epsg(3857);

    private static LineString SquareRing() => GeometryFactory.CreateLineString(
        [new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(0, 0)]);

    private static LineString TriangleRing() => GeometryFactory.CreateLineString(
        [new Coordinate(0, 0), new Coordinate(2, 0), new Coordinate(0, 2), new Coordinate(0, 0)]);

    private static Polygon SquarePolygon() => new(SquareRing(), [GeometryFactory.CreateLineString(
        [new Coordinate(0.2, 0.2), new Coordinate(0.4, 0.2), new Coordinate(0.4, 0.4), new Coordinate(0.2, 0.4), new Coordinate(0.2, 0.2)])]);

    // ---- Point ---------------------------------------------------------------

    [Fact]
    public void Point_equal_values_are_equal_with_matching_hash()
    {
        var left = new Point(new Coordinate(1, 2), Epsg4326);
        var right = new Point(new Coordinate(1, 2), Epsg4326);

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Point_same_crs_differing_coordinate_is_not_equal()
    {
        var left = new Point(new Coordinate(1, 2), Epsg4326);
        var right = new Point(new Coordinate(3, 4), Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void Point_same_coordinate_differing_crs_is_not_equal()
    {
        var left = new Point(new Coordinate(1, 2), Epsg4326);
        var right = new Point(new Coordinate(1, 2), Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
        Assert.False(left.Equals(new Point(new Coordinate(1, 2))));
    }

    [Fact]
    public void Point_same_coordinate_differing_layout_is_not_equal()
    {
        var left = GeometryFactory.CreateEmptyPoint(Epsg4326, CoordinateLayout.Xy);
        var right = GeometryFactory.CreateEmptyPoint(Epsg4326, CoordinateLayout.Xyz);

        Assert.False(left.Equals(right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- LineString ------------------------------------------------------------

    [Fact]
    public void LineString_equal_values_are_equal_across_sequence_backings()
    {
        var packed = new LineString(
            PackedCoordinateSequence.FromCoordinates([new Coordinate(0, 0), new Coordinate(1, 1)]), Epsg4326);
        var array = new LineString(
            new ArrayCoordinateSequence([new Coordinate(0, 0), new Coordinate(1, 1)], CoordinateLayout.Xy), Epsg4326);

        Assert.True(packed.Equals(array));
        Assert.True(packed.Equals((object)array));
        Assert.True(GeometryComparer.Equals(packed, array));
        Assert.Equal(packed.GetHashCode(), array.GetHashCode());
    }

    [Fact]
    public void LineString_same_crs_differing_sequence_is_not_equal()
    {
        var left = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Epsg4326);
        var right = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(2, 2)], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void LineString_same_sequence_differing_crs_is_not_equal()
    {
        var left = new LineString(
            PackedCoordinateSequence.FromCoordinates([new Coordinate(0, 0), new Coordinate(1, 1)]), Epsg4326);
        var right = new LineString(
            PackedCoordinateSequence.FromCoordinates([new Coordinate(0, 0), new Coordinate(1, 1)]), Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
        Assert.False(left.Equals(new LineString(
            PackedCoordinateSequence.FromCoordinates([new Coordinate(0, 0), new Coordinate(1, 1)]))));
    }

    // ---- Polygon ---------------------------------------------------------------

    [Fact]
    public void Polygon_equal_values_are_equal_with_matching_hash()
    {
        var left = SquarePolygon();
        var right = SquarePolygon();

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Polygon_same_rings_differing_crs_is_not_equal()
    {
        var left = new Polygon(SquareRing(), null, Epsg4326);
        var right = new Polygon(SquareRing(), null, Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void Polygon_same_crs_differing_exterior_is_not_equal()
    {
        var left = new Polygon(SquareRing(), null, Epsg4326);
        var right = new Polygon(TriangleRing(), null, Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void Polygon_same_exterior_differing_interior_is_not_equal()
    {
        var hole = GeometryFactory.CreateLineString(
            [new Coordinate(0.2, 0.2), new Coordinate(0.4, 0.2), new Coordinate(0.4, 0.4), new Coordinate(0.2, 0.4), new Coordinate(0.2, 0.2)]);
        var left = new Polygon(SquareRing(), null, Epsg4326);
        var right = new Polygon(SquareRing(), [hole], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(right.Equals(left));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- MultiPoint ------------------------------------------------------------

    [Fact]
    public void MultiPoint_equal_values_are_equal_with_matching_hash()
    {
        var left = new MultiPoint([new Point(new Coordinate(1, 2)), new Point(new Coordinate(3, 4))], Epsg4326);
        var right = new MultiPoint([new Point(new Coordinate(1, 2)), new Point(new Coordinate(3, 4))], Epsg4326);

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void MultiPoint_same_crs_differing_points_is_not_equal()
    {
        var left = new MultiPoint([new Point(new Coordinate(1, 2))], Epsg4326);
        var right = new MultiPoint([new Point(new Coordinate(9, 9))], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void MultiPoint_same_points_differing_crs_is_not_equal()
    {
        var left = new MultiPoint([new Point(new Coordinate(1, 2))], Epsg4326);
        var right = new MultiPoint([new Point(new Coordinate(1, 2))], Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- MultiLineString --------------------------------------------------------

    [Fact]
    public void MultiLineString_equal_values_are_equal_with_matching_hash()
    {
        var left = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])], Epsg4326);
        var right = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])], Epsg4326);

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void MultiLineString_same_crs_differing_parts_is_not_equal()
    {
        var left = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])], Epsg4326);
        var right = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 5)])], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void MultiLineString_same_parts_differing_crs_is_not_equal()
    {
        var left = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])], Epsg4326);
        var right = new MultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])], Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- MultiPolygon -----------------------------------------------------------

    [Fact]
    public void MultiPolygon_equal_values_are_equal_with_matching_hash()
    {
        var left = new MultiPolygon([SquarePolygon()], Epsg4326);
        var right = new MultiPolygon([SquarePolygon()], Epsg4326);

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void MultiPolygon_same_crs_differing_parts_is_not_equal()
    {
        var left = new MultiPolygon([new Polygon(SquareRing())], Epsg4326);
        var right = new MultiPolygon([new Polygon(TriangleRing())], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void MultiPolygon_same_parts_differing_crs_is_not_equal()
    {
        var left = new MultiPolygon([new Polygon(SquareRing())], Epsg4326);
        var right = new MultiPolygon([new Polygon(SquareRing())], Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- GeometryCollection -----------------------------------------------------

    [Fact]
    public void GeometryCollection_equal_values_are_equal_with_matching_hash()
    {
        var left = new GeometryCollection(
            [new Point(new Coordinate(1, 2)), GeometryFactory.CreateLineString([new Coordinate(0, 0)])], Epsg4326);
        var right = new GeometryCollection(
            [new Point(new Coordinate(1, 2)), GeometryFactory.CreateLineString([new Coordinate(0, 0)])], Epsg4326);

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.True(GeometryComparer.Equals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void GeometryCollection_same_crs_differing_parts_is_not_equal()
    {
        var left = new GeometryCollection([new Point(new Coordinate(1, 2))], Epsg4326);
        var right = new GeometryCollection([new Point(new Coordinate(9, 9))], Epsg4326);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    [Fact]
    public void GeometryCollection_same_parts_differing_crs_is_not_equal()
    {
        var left = new GeometryCollection([new Point(new Coordinate(1, 2))], Epsg4326);
        var right = new GeometryCollection([new Point(new Coordinate(1, 2))], Epsg3857);

        Assert.False(left.Equals(right));
        Assert.False(left.Equals((object)right));
        Assert.False(GeometryComparer.Equals(left, right));
    }

    // ---- Overload guards + dedupe -------------------------------------------------

    [Fact]
    public void Typed_and_object_equals_reject_null_and_foreign_types()
    {
        var point = new Point(new Coordinate(1, 2), Epsg4326);
        var line = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Epsg4326);
        var polygon = new Polygon(SquareRing(), null, Epsg4326);
        var multiPoint = new MultiPoint([point], Epsg4326);
        var multiLine = new MultiLineString([line], Epsg4326);
        var multiPolygon = new MultiPolygon([polygon], Epsg4326);
        var collection = new GeometryCollection([point], Epsg4326);

        Assert.False(point.Equals((Point?)null));
        Assert.False(line.Equals((LineString?)null));
        Assert.False(polygon.Equals((Polygon?)null));
        Assert.False(multiPoint.Equals((MultiPoint?)null));
        Assert.False(multiLine.Equals((MultiLineString?)null));
        Assert.False(multiPolygon.Equals((MultiPolygon?)null));
        Assert.False(collection.Equals((GeometryCollection?)null));

        Assert.False(point.Equals((object)line));
        Assert.False(line.Equals((object)point));
        Assert.False(polygon.Equals((object)line));
        Assert.False(multiPoint.Equals((object)multiLine));
        Assert.False(multiLine.Equals((object)multiPolygon));
        Assert.False(multiPolygon.Equals((object)multiPoint));
        Assert.False(collection.Equals((object)point));
        Assert.False(point.Equals((object?)null));
    }

    [Fact]
    public void Comparer_backed_dedupe_keeps_crs_distinct_values_apart()
    {
        var set = new HashSet<IGeometry>(GeometryComparer.Instance)
        {
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Epsg4326),
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Epsg4326),
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], Epsg3857),
        };

        Assert.Equal(2, set.Count);
    }
}
