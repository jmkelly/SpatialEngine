using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Tolerance and fidelity tests (plan §16 Phase 7): the adapter preserves
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
            TransformationArguments.Geometry, empty,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:32632");

        var transformed = Assert.IsType<Point>(Assert.IsType<CapabilitySuccess>(result).Value);
        Assert.True(transformed.IsEmpty);
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        Assert.Equal(CoordinateLayout.Xyz, transformed.Layout);
    }

    [Fact]
    public async Task Z_and_M_ordinates_survive_the_planar_transform()
    {
        var point = GeometryFactory.CreatePoint(13.405, 52.52, 100.0, 42.0, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            TransformationArguments.Geometry, point,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:32632");

        var transformed = Assert.IsType<Point>(Assert.IsType<CapabilitySuccess>(result).Value);
        Assert.Equal(100.0, transformed.Z!.Value);
        Assert.Equal(42.0, transformed.M!.Value);
        Assert.Equal(CoordinateLayout.Xyzm, transformed.Layout);
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
            TransformationArguments.Geometry, polygon,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:32632");

        var transformed = Assert.IsType<Polygon>(Assert.IsType<CapabilitySuccess>(result).Value);
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
            TransformationArguments.Geometry, polygon,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:32632");

        var transformed = Assert.IsType<Polygon>(Assert.IsType<CapabilitySuccess>(result).Value);
        Assert.True(transformed.IsEmpty);
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
        Assert.Equal(CoordinateLayout.Xyz, transformed.ExteriorRing.Layout);
    }

    [Fact]
    public async Task The_source_CRS_defaults_to_the_geometry_identity_when_omitted()
    {
        var point = GeometryFactory.CreatePoint(13.405, 52.52, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            TransformationArguments.Geometry, point,
            TransformationArguments.Target, "EPSG:32632");

        var transformed = Assert.IsType<Point>(Assert.IsType<CapabilitySuccess>(result).Value);
        AssertClose(ControlPoints.BerlinUtm32.X, transformed.X!.Value, 0.01);
    }

    [Fact]
    public async Task Describe_round_trips_through_the_descriptor_examples()
    {
        // The describe contract's embedded examples are geometry-free and live
        // with the contract (ADR-0027); every one must be served as declared.
        foreach (var example in CrsDescribeContract.Descriptor.Examples)
        {
            var result = await DescribeAsync(example.Arguments!.SelectMany(pair => new object?[] { pair.Key, pair.Value }).ToArray());
            if (example.Name == "unknown-crs" || example.Name == "missing-crs")
            {
                Assert.Equal(CapabilityErrorKind.InvalidArguments, Assert.IsType<CapabilityFailure>(result).Error.Kind);
            }
            else
            {
                Assert.IsType<CrsDescription>(Assert.IsType<CapabilitySuccess>(result).Value);
            }
        }
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}.");
    }
}
