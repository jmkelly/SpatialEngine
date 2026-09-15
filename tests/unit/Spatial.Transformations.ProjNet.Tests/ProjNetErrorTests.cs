using Spatial.Contracts;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Error-behaviour tests: unknown, unsupported or malformed CRS identities,
/// missing or conflicting arguments and out-of-area coordinates are
/// <c>invalid.arguments</c> with actionable messages; a pre-cancelled call
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class ProjNetErrorTests
{
    [Fact]
    public async Task Describing_an_unknown_EPSG_code_is_an_invalid_argument()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() => DescribeAsync("crs", "EPSG:999999"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("999999", exception.Message);
    }

    [Fact]
    public async Task Describing_a_malformed_identity_is_an_invalid_argument()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() => DescribeAsync("crs", "not-an-identity"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("CRS identity", exception.Message);
    }

    [Fact]
    public async Task Describing_without_a_crs_argument_is_an_invalid_argument()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() => DescribeAsync());

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'crs'", exception.Message);
    }

    [Fact]
    public async Task An_unsupported_authority_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "source", "ESRI:102100",
            "target", "EPSG:4326"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("not served", exception.Message);
    }

    [Fact]
    public async Task Transforming_without_a_target_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync("geometry", geometry));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'target'", exception.Message);
    }

    [Fact]
    public async Task Transforming_without_a_source_when_the_geometry_has_no_CRS_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "target", "EPSG:4326"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'source'", exception.Message);
    }

    [Fact]
    public async Task A_source_argument_conflicting_with_the_geometry_CRS_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(32632));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "source", "EPSG:4326",
            "target", "EPSG:3857"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("agree", exception.Message);
    }

    [Fact]
    public async Task A_non_string_target_argument_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "target", 4326));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'target'", exception.Message);
    }

    [Fact]
    public async Task Coordinates_outside_the_valid_area_are_an_actionable_error()
    {
        // Latitude 95 lies beyond the poles; the Web Mercator formula maps it
        // to a non-finite northing, which the service reports as an invalid
        // argument rather than returning poisoned geometry.
        var geometry = GeometryFactory.CreatePoint(0, 95, CoordinateReference.Epsg(4326));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "source", "EPSG:4326",
            "target", "EPSG:3857"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("valid area", exception.Message);
    }

    [Fact]
    public async Task Transforming_to_a_malformed_target_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "geometry", geometry,
            "source", "EPSG:4326",
            "target", "not-an-identity"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'target' must be a CRS identity", exception.Message);
    }

    [Fact]
    public async Task Transforming_without_a_geometry_is_an_invalid_argument()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() => TransformAsync(
            "source", "EPSG:4326",
            "target", "EPSG:3857"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'geometry'", exception.Message);
    }

    [Fact]
    public async Task A_pre_cancelled_call_throws_operation_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        await Assert.ThrowsAsync<OperationCanceledException>(() => InvokeAsync(
            null,
            Arguments(
                "geometry", geometry,
                "source", "EPSG:4326",
                "target", "EPSG:3857"),
            cancellation.Token));
    }
}
