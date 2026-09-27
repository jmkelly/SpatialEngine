using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The ground-distance (geodesic) buffer of ADR-0075: a working plane sized
/// by the work's own extent, reproducing a geodesic buffer within the
/// tolerance the ADR states. The reference is a Vincenty inverse on WGS 84
/// computed here, so the test does not trust the projection it is checking.
/// </summary>
public sealed class ProjNetGeodesicBufferingTests
{
    /// <summary>The ADR's stated tolerance, as a multiplier on the distance.</summary>
    private const double Tolerance = ProjNetGeodesicBuffering.StatedRelativeTolerance;

    private static readonly NtsGeometryOperations Planar = new();
    private static readonly NtsGeometryProcessing Processing = new();
    private static readonly ProjNetGeodesicBuffering Buffering = new(Planar, Processing);

    [Theory]
    [InlineData(13.405, 52.52)]
    [InlineData(-0.1276, 51.5072)]
    [InlineData(151.2093, -33.8688)]
    [InlineData(-70.6693, -33.4489)]
    [InlineData(0.0, 0.0)]
    [InlineData(2.3522, 48.8566)]
    [InlineData(179.5, 0.0)]
    [InlineData(-179.5, 0.0)]
    [InlineData(0.0, 84.0)]
    public void A_buffered_point_holds_the_geodesic_circle(double longitude, double latitude)
    {
        var centre = new Coordinate(longitude, latitude);
        var buffered = Buffering.Buffer(new Point(centre, CoordinateReference.Epsg(4326)), 1000.0);

        var polygon = Assert.IsType<Polygon>(buffered);
        Assert.Equal(CoordinateReference.Epsg(4326), polygon.CoordinateReference);

        // Every boundary vertex must sit on the 1000 m geodesic circle, which
        // is the point of the whole exercise: a planar degree buffer would
        // put it anywhere between 1 km and 111 km away.
        foreach (var vertex in polygon.ExteriorRing.Coordinates())
        {
            var distance = Geodesic.DistanceMetres(longitude, latitude, vertex.X, vertex.Y);
            Assert.InRange(distance, 1000.0 * (1 - Tolerance), 1000.0 * (1 + Tolerance));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void A_buffered_point_holds_the_geodesic_circle_at_every_quadrant_segment_count(int quadrantSegments)
    {
        var buffered = Buffering.Buffer(new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326)), 1000.0, quadrantSegments);

        foreach (var vertex in Assert.IsType<Polygon>(buffered).ExteriorRing.Coordinates())
        {
            var distance = Geodesic.DistanceMetres(13.405, 52.52, vertex.X, vertex.Y);
            Assert.InRange(distance, 1000.0 * (1 - Tolerance), 1000.0 * (1 + Tolerance));
        }
    }

    [Fact]
    public void A_buffer_agrees_with_the_same_buffer_done_in_a_projected_crs()
    {
        // The projected path (transform, planar buffer, transform back) is
        // the answer a caller can already reach with a bufferSR, so the two
        // must not disagree about where the edge is. Vertex-for-vertex
        // equality is not the claim — the two rings put their vertices at
        // different places on the same circle — so both are measured against
        // the geodesic circle itself. The UTM reference carries its own
        // error (k0 = 0.9996 plus the zone's projection distortion), which is
        // why it is held to a looser bound than the geodesic verb.
        var transforms = new ProjNetTransforms();
        var point = new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326));

        var geodesic = Buffering.Buffer(point, 1000.0);
        var projected = Planar.Buffer(
            transforms.Transform(point, null, "EPSG:25832", CancellationToken.None),
            1000.0,
            8,
            CancellationToken.None);
        var viaUtm = transforms.Transform(projected, null, "EPSG:4326", CancellationToken.None);

        AssertOnCircle(geodesic, 13.405, 52.52, 1000.0, Tolerance);
        AssertOnCircle(viaUtm, 13.405, 52.52, 1000.0, 0.002);
    }

    [Fact]
    public void A_negative_distance_erodes_along_the_geodesic()
    {
        // A square wide enough that eroding it leaves a ring: each side
        // moves in by the distance measured on the ground, which is the
        // part a degree-buffer would get wrong.
        const double HalfSize = 0.05;
        var square = Square(13.405, 52.52, HalfSize);
        var eroded = Assert.IsType<Polygon>(Buffering.Buffer(square, -500.0));
        Assert.Equal(CoordinateReference.Epsg(4326), eroded.CoordinateReference);

        // The south side's midpoint: 500 m closer to the centre than it was.
        var before = Geodesic.DistanceMetres(13.405, 52.52, 13.405, 52.52 - HalfSize);
        var midpoint = Midpoint(eroded, 1);
        var after = Geodesic.DistanceMetres(13.405, 52.52, midpoint.X, midpoint.Y);
        Assert.InRange(after, (before - 500.0) * (1 - Tolerance), (before - 500.0) * (1 + Tolerance));
    }

    [Fact]
    public void A_multi_point_buffers_each_part_in_its_own_working_plane()
    {
        // Two points 400 km apart: one working plane for the whole geometry
        // would exceed the stated limit, but each part on its own is small.
        var multiPoint = new MultiPoint(
            [
                new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326)),
                new Point(new Coordinate(13.405, 56.52), CoordinateReference.Epsg(4326)),
            ],
            CoordinateReference.Epsg(4326));

        var buffered = Buffering.Buffer(multiPoint, 1000.0);

        var multiPolygon = Assert.IsType<MultiPolygon>(buffered);
        Assert.Equal(2, multiPolygon.Polygons.Count);
        foreach (var vertex in multiPolygon.Coordinates())
        {
            var distance = Math.Min(
                Geodesic.DistanceMetres(13.405, 52.52, vertex.X, vertex.Y),
                Geodesic.DistanceMetres(13.405, 56.52, vertex.X, vertex.Y));
            Assert.InRange(distance, 1000.0 * (1 - Tolerance), 1000.0 * (1 + Tolerance));
        }
    }

    [Fact]
    public void Overlapping_parts_dissolve_into_one_valid_polygon()
    {
        // Two squares 500 m apart, each buffered by 1 km: the parts overlap
        // after buffering, so the result must not self-overlap.
        var multiPolygon = new MultiPolygon(
            [
                Square(13.405, 52.52, 0.004),
                Square(13.41, 52.52, 0.004),
            ],
            CoordinateReference.Epsg(4326));

        var buffered = Buffering.Buffer(multiPolygon, 1000.0);

        var dissolved = Assert.IsType<Polygon>(buffered);
        Assert.True(Planar.Validate(dissolved), "the dissolved buffer must be a valid geometry");
    }

    [Fact]
    public void An_extent_past_the_stated_limit_is_refused_by_name()
    {
        // A city-sized feature buffered by 500 km: the working plane would
        // not hold the stated tolerance, so the engine says so rather than
        // answering with a wrong shape.
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(Square(13.405, 52.52, 0.01), 500_000.0));

        Assert.Equal("invalid.arguments", exception.Code);
        Assert.Contains("working radius", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bufferSR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_polar_input_is_refused_by_name()
    {
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(new Point(new Coordinate(0.0, 89.9), CoordinateReference.Epsg(4326)), 1000.0));

        Assert.Equal("invalid.arguments", exception.Code);
        Assert.Contains("pole", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projected_input_is_refused_by_name()
    {
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(new Point(new Coordinate(798812.8, 5827999.9), CoordinateReference.Epsg(25832)), 1000.0));

        Assert.Equal("invalid.arguments", exception.Code);
        Assert.Contains("geographic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_geometry_without_a_spatial_reference_is_refused_by_name()
    {
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(new Point(new Coordinate(13.405, 52.52), null), 1000.0));

        Assert.Equal("invalid.arguments", exception.Code);
        Assert.Contains("spatial reference", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_bad_distance_is_refused_by_name(double distance)
    {
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326)), distance));

        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public void A_non_positive_quadrant_segment_count_is_refused_by_name()
    {
        var exception = Assert.Throws<SpatialException>(
            () => Buffering.Buffer(new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326)), 1000.0, 0));

        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public void Cancellation_stops_the_buffer()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Buffering.Buffer(new Point(new Coordinate(13.405, 52.52), CoordinateReference.Epsg(4326)), 1000.0, 8, cancelled.Token));
    }

    private static void AssertOnCircle(IGeometry buffered, double longitude, double latitude, double radius, double tolerance)
    {
        var vertices = buffered.Coordinates().ToArray();
        Assert.NotEmpty(vertices);
        foreach (var vertex in vertices)
        {
            var distance = Geodesic.DistanceMetres(longitude, latitude, vertex.X, vertex.Y);
            Assert.InRange(distance, radius * (1 - tolerance), radius * (1 + tolerance));
        }
    }

    /// <summary>The midpoint of one square side, by corner index.</summary>
    private static Coordinate Midpoint(Polygon square, int side)
    {
        var corners = square.ExteriorRing.Coordinates().ToArray();
        var start = corners[side % (corners.Length - 1)];
        var end = corners[(side + 1) % (corners.Length - 1)];
        return new Coordinate((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
    }

    private static Polygon Square(double longitude, double latitude, double halfSizeDegrees) =>
        GeometryFactory.CreatePolygon(
            [
                new Coordinate(longitude - halfSizeDegrees, latitude - halfSizeDegrees),
                new Coordinate(longitude + halfSizeDegrees, latitude - halfSizeDegrees),
                new Coordinate(longitude + halfSizeDegrees, latitude + halfSizeDegrees),
                new Coordinate(longitude - halfSizeDegrees, latitude + halfSizeDegrees),
                new Coordinate(longitude - halfSizeDegrees, latitude - halfSizeDegrees),
            ],
            CoordinateReference.Epsg(4326));
}
