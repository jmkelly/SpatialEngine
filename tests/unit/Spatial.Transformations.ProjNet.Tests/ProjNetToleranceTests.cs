using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Transformations;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Tolerance and fidelity tests (ADR-0027): the adapter preserves
/// what the planar transform does not touch — empty shapes with their
/// layouts, Z and M ordinates, multi-part structure — stamps the target CRS
/// and never disturbs coordinates it does not transform.
/// </summary>
public sealed class ProjNetToleranceTests
{
    [Fact]
    public async Task An_empty_geometry_transforms_to_an_empty_geometry_in_the_target_CRS()
    {
        var empty = GeometryFactory.CreateEmptyPoint(CoordinateReference.Epsg(4326), CoordinateLayout.Xyz);

        var result = await TransformAsync(
            "geometry", empty,
            "source", "EPSG:4326",
            "target", "EPSG:32632");

        var transformed = Assert.IsType<Point>(result);
        Assert.True(transformed.IsEmpty);
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        Assert.Equal(CoordinateLayout.Xyz, transformed.Layout);
    }

    [Fact]
    public async Task Z_and_M_ordinates_survive_the_planar_transform()
    {
        var point = GeometryFactory.CreatePoint(13.405, 52.52, 100.0, 42.0, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            "geometry", point,
            "source", "EPSG:4326",
            "target", "EPSG:32632");

        var transformed = Assert.IsType<Point>(result);
        Assert.Equal(100.0, transformed.Z!.Value);
        Assert.Equal(42.0, transformed.M!.Value);
        Assert.Equal(CoordinateLayout.Xyzm, transformed.Layout);
    }

    [Fact]
    public async Task Every_geometry_family_transforms_and_keeps_its_shape()
    {
        // Top-level line strings hit the LineString arm of the transform;
        // the four composites exercise the shared composite path.
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(13.35, 52.50), new Coordinate(13.45, 52.55), new Coordinate(13.55, 52.50)],
            CoordinateReference.Epsg(4326));
        var multiPoint = new MultiPoint(
            [GeometryFactory.CreatePoint(13.35, 52.50, CoordinateReference.Epsg(4326)),
             GeometryFactory.CreatePoint(13.45, 52.55, CoordinateReference.Epsg(4326))],
            CoordinateReference.Epsg(4326));
        var multiLine = new MultiLineString([line], CoordinateReference.Epsg(4326));
        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(13.35, 52.50),
            new Coordinate(13.45, 52.50),
            new Coordinate(13.45, 52.55),
            new Coordinate(13.35, 52.55),
            new Coordinate(13.35, 52.50),
        ], CoordinateReference.Epsg(4326));
        var multiPolygon = new MultiPolygon([polygon], CoordinateReference.Epsg(4326));
        var collection = new GeometryCollection([line, polygon], CoordinateReference.Epsg(4326));

        IGeometry[] inputs = [line, multiPoint, multiLine, multiPolygon, collection];
        foreach (var input in inputs)
        {
            var result = await TransformAsync(
                "geometry", input,
                "source", "EPSG:4326",
                "target", "EPSG:32632");
            var transformed = result;
            Assert.Same(input.GetType(), transformed.GetType());
            Assert.False(transformed.IsEmpty);
            Assert.Equal(input.CoordinateCount, transformed.CoordinateCount);
            Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        }
    }

    [Fact]
    public async Task Empty_composites_and_lines_keep_their_type_in_the_target_CRS()
    {
        var emptyLine = GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xyz, CoordinateReference.Epsg(4326));
        var emptyMultiPoint = new MultiPoint([], CoordinateReference.Epsg(4326));
        var emptyCollection = new GeometryCollection([], CoordinateReference.Epsg(4326));

        foreach (var input in new IGeometry[] { emptyLine, emptyMultiPoint, emptyCollection })
        {
            var result = await TransformAsync(
                "geometry", input,
                "source", "EPSG:4326",
                "target", "EPSG:32632");
            var transformed = result;
            Assert.Same(input.GetType(), transformed.GetType());
            Assert.True(transformed.IsEmpty);
            Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        }
    }

    [Fact]
    public async Task Polygon_rings_and_multi_part_collections_are_transformed_recursively()
    {
        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(13.35, 52.50),
            new Coordinate(13.45, 52.50),
            new Coordinate(13.45, 52.55),
            new Coordinate(13.35, 52.55),
            new Coordinate(13.35, 52.50),
        ], CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            "geometry", polygon,
            "source", "EPSG:4326",
            "target", "EPSG:32632");

        var transformed = Assert.IsType<Polygon>(result);
        Assert.Equal(5, transformed.ExteriorRing.CoordinateCount);
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        Assert.False(transformed.IsEmpty);
        // The leading ring vertex must be a real transformed coordinate, not the origin.
        Assert.True(Math.Abs(transformed.ExteriorRing.Sequence.GetX(0)) > 100_000);
    }

    [Fact]
    public async Task An_empty_polygon_keeps_its_ring_layout_and_target_CRS()
    {
        var empty = GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xyz, CoordinateReference.Epsg(4326));
        var polygon = new Polygon(empty, null, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            "geometry", polygon,
            "source", "EPSG:4326",
            "target", "EPSG:32632");

        var transformed = Assert.IsType<Polygon>(result);
        Assert.True(transformed.IsEmpty);
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        Assert.Equal(CoordinateLayout.Xyz, transformed.ExteriorRing.Layout);
    }

    [Fact]
    public async Task The_source_CRS_defaults_to_the_geometry_identity_when_omitted()
    {
        var point = GeometryFactory.CreatePoint(13.405, 52.52, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            "geometry", point,
            "target", "EPSG:32632");

        var transformed = Assert.IsType<Point>(result);
        AssertClose(ControlPoints.BerlinUtm32.X, transformed.X!.Value, 0.01);
    }

    [Fact]
    public async Task Describe_serves_known_codes_and_rejects_unknown_ones()
    {
        var description = await DescribeAsync("crs", "EPSG:4326");
        Assert.Equal("4326", description.Code);

        await Assert.ThrowsAsync<SpatialException>(() => DescribeAsync("crs", "EPSG:999999"));
        await Assert.ThrowsAsync<SpatialException>(() => DescribeAsync());
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}.");
    }
}
