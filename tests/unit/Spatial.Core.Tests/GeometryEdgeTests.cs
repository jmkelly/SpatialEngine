using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Mutation-resistance assertions for the sequence, comparer, traversal and
/// factory surfaces: exact exception messages, per-component equality, hash
/// consistency through the comparer and the unused builder/factory overloads.
/// </summary>
public class GeometryEdgeTests
{
    // ---- Coordinate sequences -------------------------------------------------

    [Fact]
    public void Array_sequence_error_messages_are_exact()
    {
        var sequence = new ArrayCoordinateSequence(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2)],
            CoordinateLayout.Xy);

        var index = Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(3, Ordinate.X));
        Assert.Contains("Index 3 is out of range for a sequence of 3 coordinates", index.Message);
        Assert.Contains("Index -1 is out of range", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(-1, Ordinate.X)).Message);

        Assert.Contains("Layout Xy does not store Z", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.Z)).Message);
        Assert.Contains("Layout Xy does not store M", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.M)).Message);
        Assert.Contains("Unknown ordinate '42'", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, (Ordinate)42)).Message);
        Assert.Equal("ArrayCoordinateSequence [3 × Xy]", sequence.ToString());
    }

    [Fact]
    public void Packed_sequence_error_messages_are_exact()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);

        var index = Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(2, Ordinate.X));
        Assert.Contains("Index 2 is out of range for a sequence of 2 coordinates", index.Message);

        Assert.Contains("Packed sequence length 5 is not a multiple of stride 2",
            Assert.Throws<ArgumentException>(() => new PackedCoordinateSequence(new double[5], CoordinateLayout.Xy)).Message);
        Assert.Contains("Layout Xy does not store Z", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.Z)).Message);
        Assert.Contains("Layout Xy does not store M", Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.M)).Message);
        Assert.Contains("Coordinate at index 1 has M=7 but layout Xy does not store M",
            Assert.Throws<ArgumentException>(() => PackedCoordinateSequence.FromCoordinates([new(0, 0), new(1, 2, M: 7)], CoordinateLayout.Xy)).Message);
        Assert.Equal("PackedCoordinateSequence [2 × Xy]", sequence.ToString());
    }

    [Fact]
    public void Sequence_extension_accessors_named_per_ordinate()
    {
        var xyz = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: 5)], CoordinateLayout.Xyz);
        Assert.Equal(1, xyz.GetX(0));
        Assert.Equal(2, xyz.GetY(0));
        Assert.Equal(5, xyz.GetZ(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => xyz.GetM(0));

        var xym = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, M: 6)], CoordinateLayout.Xym);
        Assert.Equal(6, xym.GetM(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => xym.GetZ(0));
    }

    // ---- GeometryComparer -----------------------------------------------------

    [Fact]
    public void Comparer_hash_equality_holds_for_every_concrete_type()
    {
        var crs = CoordinateReference.Epsg(4326);
        IGeometry point = GeometryFactory.CreatePoint(1, 2, 3, crs);
        IGeometry line = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], crs);
        IGeometry polygon = GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(0, 1), new Coordinate(0, 0)]));
        IGeometry multiPoint = GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(1, 2), GeometryFactory.CreateEmptyPoint());
        IGeometry multiLine = GeometryFactory.CreateMultiLineString(GeometryFactory.CreateLineString([new Coordinate(0, 0)]));
        IGeometry multiPolygon = GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(0, 1), new Coordinate(0, 0)])));
        IGeometry collection = GeometryFactory.CreateGeometryCollection(point, line);

        foreach (var geometry in new[] { point, line, polygon, multiPoint, multiLine, multiPolygon, collection })
        {
            Assert.Equal(geometry.GetHashCode(), GeometryComparer.GetHashCode(geometry));
        }
    }

    [Fact]
    public void Comparer_unknown_geometry_type_reports_the_type()
    {
        var foreign = new ForeignGeometry();
        var twin = new ForeignGeometry();
        Assert.False(GeometryComparer.Equals(foreign, twin));
        Assert.False(GeometryComparer.Equals(foreign, null));
        Assert.True(GeometryComparer.Equals(foreign, foreign)); // same reference
        var exception = Assert.Throws<ArgumentException>(() => GeometryComparer.GetHashCode(foreign));
        Assert.Contains("Unknown geometry type '200'", exception.Message);
    }

    // ---- GeometryTraversal ----------------------------------------------------

    [Fact]
    public void Traversal_yields_leaf_coordinates_for_every_leaf_type()
    {
        Assert.Equal([new Coordinate(7, 8)], GeometryFactory.CreatePoint(7, 8).Coordinates());
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        Assert.Equal([new Coordinate(1, 2), new Coordinate(3, 4)], line.Coordinates());
        var emptyPoint = GeometryFactory.CreateEmptyPoint();
        Assert.Equal(new IGeometry[] { emptyPoint }, emptyPoint.DepthFirst()); // DepthFirst always yields at least the geometry itself
    }

    [Fact]
    public void Traversal_parts_of_multi_types()
    {
        var a = GeometryFactory.CreatePoint(1, 2);
        var b = GeometryFactory.CreatePoint(3, 4);
        var multiPoint = GeometryFactory.CreateMultiPoint(a, b);
        Assert.Equal(new IGeometry[] { a, b }, multiPoint.Parts());
        // DepthFirst yields the composite itself first, then each child.
        Assert.Equal(new IGeometry[] { multiPoint, a, b }, multiPoint.DepthFirst());
    }

    // ---- GeometryFactory overloads --------------------------------------------

    [Fact]
    public void CreatePolygon_from_sequences_supports_interior_rings()
    {
        var exterior = PackedCoordinateSequence.FromCoordinates(
            [new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]);
        var hole = PackedCoordinateSequence.FromCoordinates(
            [new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)]);

        var polygon = GeometryFactory.CreatePolygon(exterior);
        Assert.False(polygon.IsEmpty);
        Assert.Equal(5, polygon.CoordinateCount);
        Assert.Empty(polygon.InteriorRings);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);

        var withHole = GeometryFactory.CreatePolygon(exterior, [hole]);
        Assert.Single(withHole.InteriorRings);
        Assert.Equal(10, withHole.CoordinateCount);

        Assert.Throws<ArgumentNullException>(() => GeometryFactory.CreatePolygon((LineString)null!));
        Assert.Throws<ArgumentNullException>(() => GeometryFactory.CreatePolygon((ICoordinateSequence)null!));
    }

    [Fact]
    public void CreatePolygon_conflicting_crs_is_rejected()
    {
        var epsg = CoordinateReference.Epsg(4326);
        var other = CoordinateReference.Epsg(3857);
        var exterior = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], epsg);
        var hole = GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], other);

        Assert.Throws<ArgumentException>(() => GeometryFactory.CreatePolygon(exterior, [hole]));
        Assert.Throws<ArgumentException>(() => GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(exterior),
            GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)], other))));
    }

    // ---- Collection semantics --------------------------------------------------

    [Fact]
    public void Collections_are_empty_when_every_child_is_empty()
    {
        var empty = GeometryFactory.CreateGeometryCollection(GeometryFactory.CreateEmptyPoint(), GeometryFactory.CreateEmptyPoint());
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.CoordinateCount);

        var mixed = GeometryFactory.CreateGeometryCollection(GeometryFactory.CreateEmptyPoint(), GeometryFactory.CreatePoint(1, 2));
        Assert.False(mixed.IsEmpty);
        Assert.Equal(1, mixed.CoordinateCount);

        Assert.True(GeometryFactory.CreateGeometryCollection().IsEmpty);
        Assert.True(GeometryFactory.CreateMultiLineString(GeometryFactory.CreateEmptyLineString()).IsEmpty);
        Assert.True(GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString()),
            GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString())).IsEmpty);
    }

    [Fact]
    public void Composite_equals_object_overloads_reject_other_types()
    {
        var point = GeometryFactory.CreatePoint(1, 2);
        var line = GeometryFactory.CreateLineString([new Coordinate(1, 2)]);
        var polygon = GeometryFactory.CreatePolygon(line);
        var multiPoint = GeometryFactory.CreateMultiPoint(point, GeometryFactory.CreateEmptyPoint());
        var multiLine = GeometryFactory.CreateMultiLineString(line);
        var multiPolygon = GeometryFactory.CreateMultiPolygon(polygon);
        var collection = GeometryFactory.CreateGeometryCollection(point, line);

        Assert.False(multiPoint.Equals((object)line));
        Assert.False(multiLine.Equals((object)point));
        Assert.False(multiPolygon.Equals((object)line));
        Assert.False(collection.Equals((object)point));
        Assert.False(line.Equals((object)multiPoint));
        Assert.False(polygon.Equals((object)line));
    }

    // ---- Point accessors -------------------------------------------------------

    [Fact]
    public void Point_ordinate_accessors_surface_values_and_nulls()
    {
        var empty = GeometryFactory.CreateEmptyPoint();
        Assert.Null(empty.Z);
        Assert.Null(empty.M);

        var located = GeometryFactory.CreatePoint(1, 2, 3, 4);
        Assert.Equal(1, located.X);
        Assert.Equal(2, located.Y);
        Assert.Equal(3, located.Z);
        Assert.Equal(4, located.M);

        var zOnly = GeometryFactory.CreatePoint(1, 2, 3);
        Assert.Equal(3, zOnly.Z);
        Assert.Null(zOnly.M);

        Assert.Equal("Point (1, 2)", GeometryFactory.CreatePoint(1, 2).ToString());
        Assert.Equal("Point (empty)", empty.ToString());
    }
}

/// <summary>An IGeometry implementation claiming a type Spatial.Core does not support.</summary>
public sealed class ForeignGeometry : IGeometry
{
    public GeometryType Type => (GeometryType)200;
    public CoordinateLayout Layout => CoordinateLayout.Xy;
    public CoordinateReference? CoordinateReference => null;
    public bool IsEmpty => true;
    public int CoordinateCount => 0;
    public Envelope? Envelope => null;
}
