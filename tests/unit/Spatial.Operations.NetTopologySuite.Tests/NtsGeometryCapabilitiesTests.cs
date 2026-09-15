using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// Behaviour of the measurement, set/construction and relation services
/// (ADR-0036): the granular verbs the GeoServices adapter maps onto.
/// </summary>
public sealed class NtsGeometryCapabilitiesTests
{
    private readonly NtsGeometryMeasures _measures = new();
    private readonly NtsGeometryProcessing _processing = new();
    private readonly NtsGeometryRelations _relations = new();
    private readonly NtsGeometryOperations _operations = new();

    [Fact]
    public void Area_and_length_measure_planar_shapes()
    {
        Assert.Equal(4.0, _measures.Area(Square(0, 0, 2, 2)), 9);
        Assert.Equal(5.0, _measures.Length(GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(3, 4)])), 9);
    }

    [Fact]
    public void Area_of_an_empty_geometry_is_zero()
    {
        Assert.Equal(0.0, _measures.Area(GeometryFactory.CreateEmptyPoint()));
        Assert.Equal(0.0, _measures.Length(GeometryFactory.CreateEmptyLineString()));
    }

    [Fact]
    public void Distance_between_two_points_is_planar()
    {
        Assert.Equal(5.0, _measures.Distance(GeometryFactory.CreatePoint(0, 0), GeometryFactory.CreatePoint(3, 4)), 9);
    }

    [Fact]
    public void Label_point_lies_inside_a_polygon()
    {
        var label = _measures.LabelPoint(Square(0, 0, 4, 4));

        var point = Assert.IsAssignableFrom<Point>(label);
        Assert.True(point.X is > 0 and < 4);
        Assert.True(point.Y is > 0 and < 4);
    }

    [Fact]
    public void Union_merges_overlapping_polygons()
    {
        var union = _processing.Union([Square(0, 0, 2, 2), Square(1, 1, 3, 3)]);

        Assert.Equal(7.0, _measures.Area(union), 9);
    }

    [Fact]
    public void Union_of_no_geometries_is_an_invalid_argument()
    {
        Assert.Throws<SpatialException>(() => _processing.Union([]));
    }

    [Fact]
    public void Difference_subtracts_the_second_geometry()
    {
        var difference = _processing.Difference(Square(0, 0, 2, 2), Square(1, 1, 3, 3));

        Assert.Equal(3.0, _measures.Area(difference), 9);
    }

    [Fact]
    public void Convex_hull_covers_every_input()
    {
        var hull = _processing.ConvexHull(
        [
            GeometryFactory.CreatePoint(0, 0),
            GeometryFactory.CreatePoint(4, 0),
            GeometryFactory.CreatePoint(4, 4),
            GeometryFactory.CreatePoint(0, 4),
            GeometryFactory.CreatePoint(2, 2),
        ]);

        var envelope = hull.Envelope!.Value;
        Assert.Equal(0, envelope.MinX);
        Assert.Equal(4, envelope.MaxX);
        Assert.Equal(0, envelope.MinY);
        Assert.Equal(4, envelope.MaxY);
    }

    [Fact]
    public void Repair_fixes_a_self_intersecting_ring()
    {
        var bowTie = GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(2, 2), new Coordinate(2, 0), new Coordinate(0, 2), new Coordinate(0, 0)]);

        var repaired = _processing.Repair(bowTie);

        Assert.True(_operations.Validate(repaired));
    }

    [Fact]
    public void Densify_inserts_vertices()
    {
        var densified = _processing.Densify(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(0, 10)]),
            2);

        Assert.Equal(6, densified.CoordinateCount);
    }

    [Fact]
    public void Densify_rejects_a_non_positive_segment_length()
    {
        Assert.Throws<SpatialException>(() => _processing.Densify(GeometryFactory.CreatePoint(0, 0), 0));
    }

    [Fact]
    public void Relate_evaluates_a_de9im_pattern()
    {
        Assert.True(_relations.Relate(Square(0, 0, 2, 2), Square(1, 1, 3, 3), "T*T***T**"));
        Assert.False(_relations.Relate(Square(0, 0, 2, 2), Square(5, 5, 6, 6), "T*T***T**"));
    }

    [Fact]
    public void Relate_rejects_an_empty_pattern()
    {
        Assert.Throws<SpatialException>(() => _relations.Relate(GeometryFactory.CreatePoint(0, 0), GeometryFactory.CreatePoint(1, 1), " "));
    }

    private static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(minX, maxY),
            new Coordinate(maxX, maxY),
            new Coordinate(maxX, minY),
            new Coordinate(minX, minY),
        ]);
}
