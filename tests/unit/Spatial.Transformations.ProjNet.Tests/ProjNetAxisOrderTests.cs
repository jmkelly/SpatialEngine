using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Axis-order tests (ADR-0027): the engine's coordinate convention is
/// x-first for every CRS — x is longitude for geographic, easting for
/// projected — exactly the order ProjNet 2.1's math transforms consume and
/// produce, so the adapter needs no axis swaps. These tests pin that
/// convention with control points (swapped axes give different, wrong
/// results), and pin that describe reports the CRS's declared axes.
/// </summary>
public sealed class ProjNetAxisOrderTests
{
    [Fact]
    public async Task The_engine_feeds_longitude_first_into_a_zone_32_control_point()
    {
        // Zone 32's central meridian is 9°E, so a point at (lon 10, lat 50)
        // lands just east of the false origin at ~571.7 km easting. If the
        // adapter fed latitude first, the same input would land near 5.4 M m.
        var geometry = GeometryFactory.CreatePoint(10, 50, CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(geometry, "EPSG:4326", "EPSG:32632");
        AssertClose(x, 571_666.45, 0.02);
        AssertClose(y, 5_539_109.82, 0.02);
    }

    [Fact]
    public async Task Swapped_axes_produce_a_different_result_proving_the_order_matters()
    {
        var lonLat = GeometryFactory.CreatePoint(10, 50, CoordinateReference.Epsg(4326));
        var latLon = GeometryFactory.CreatePoint(50, 10, CoordinateReference.Epsg(4326));

        var first = await Transform(lonLat, "EPSG:4326", "EPSG:32632");
        var swapped = await Transform(latLon, "EPSG:4326", "EPSG:32632");
        Assert.True(
            Math.Abs(first.X - swapped.X) > 4_000_000,
            $"swapping axes must move the result; got {first.X} and {swapped.X}.");
    }

    [Fact]
    public async Task Web_mercator_proves_x_is_longitude()
    {
        // Mercator x depends only on longitude (R * lon_rad); y only on
        // latitude. x(10°, 50°) must equal x of 10°E at any latitude.
        var geometry = GeometryFactory.CreatePoint(10, 50, CoordinateReference.Epsg(4326));

        var (x, _) = await Transform(geometry, "EPSG:4326", "EPSG:3857");
        AssertClose(x, 1_113_194.91, 0.01);
    }

    [Fact]
    public async Task A_round_trip_preserves_the_original_coordinates_through_a_geographic_CRS()
    {
        var original = GeometryFactory.CreatePoint(11.572, 48.14, CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(original, "EPSG:4326", "EPSG:25832");
        var back = await Transform(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(25832)), "EPSG:25832", "EPSG:4326");
        AssertClose(original.X!.Value, back.X, 0.000001);
        AssertClose(original.Y!.Value, back.Y, 0.000001);
    }

    [Fact]
    public async Task Describe_reports_the_geographic_axes_as_lon_then_lat()
    {
        var description = await DescribeAsync("crs", "EPSG:4326");

        Assert.Equal(2, description.Axes.Count);
        Assert.Equal("Lon", description.Axes[0].Name);
        Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
        Assert.Equal("Lat", description.Axes[1].Name);
        Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
    }

    [Fact]
    public async Task Describe_reports_the_projected_axes_as_easting_then_northing()
    {
        var description = await DescribeAsync("crs", "EPSG:32632");

        Assert.Equal("Easting", description.Axes[0].Name);
        Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
        Assert.Equal("Northing", description.Axes[1].Name);
        Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
    }

    private static async Task<(double X, double Y)> Transform(IGeometry geometry, string source, string target)
    {
        var transformed = await TransformAsync(
            "geometry", geometry,
            "source", source,
            "target", target);
        var point = Assert.IsType<Point>(transformed);
        return (point.X!.Value, point.Y!.Value);
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}.");
    }
}
