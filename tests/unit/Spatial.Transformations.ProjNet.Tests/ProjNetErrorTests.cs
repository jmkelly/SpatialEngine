using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Error-behaviour tests (plan §16 Phase 7): unknown, unsupported or
/// malformed CRS identities, missing or conflicting arguments and
/// out-of-area coordinates are <c>invalid.arguments</c> with actionable
/// messages; a pre-cancelled invocation is a <c>cancelled</c> failure.
/// </summary>
public sealed class ProjNetErrorTests
{
    [Fact]
    public async Task Describing_an_unknown_EPSG_code_is_an_invalid_argument()
    {
        var result = await DescribeAsync(TransformationArguments.Crs, "EPSG:999999");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Equal("invalid.arguments", failure.Error.Code);
        Assert.Contains("999999", failure.Error.Message);
    }

    [Fact]
    public async Task Describing_a_malformed_identity_is_an_invalid_argument()
    {
        var result = await DescribeAsync(TransformationArguments.Crs, "not-an-identity");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("CRS identity", failure.Error.Message);
    }

    [Fact]
    public async Task Describing_without_a_crs_argument_is_an_invalid_argument()
    {
        var result = await DescribeAsync();

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("'crs'", failure.Error.Message);
    }

    [Fact]
    public async Task An_unsupported_authority_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            TransformationArguments.Geometry, geometry,
            TransformationArguments.Source, "ESRI:102100",
            TransformationArguments.Target, "EPSG:4326");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("not served", failure.Error.Message);
    }

    [Fact]
    public async Task Transforming_without_a_target_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(TransformationArguments.Geometry, geometry);

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("'target'", failure.Error.Message);
    }

    [Fact]
    public async Task Transforming_without_a_source_when_the_geometry_has_no_CRS_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2);

        var result = await TransformAsync(
            TransformationArguments.Geometry, geometry,
            TransformationArguments.Target, "EPSG:4326");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("'source'", failure.Error.Message);
    }

    [Fact]
    public async Task A_source_argument_conflicting_with_the_geometry_CRS_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(32632));

        var result = await TransformAsync(
            TransformationArguments.Geometry, geometry,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:3857");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("agree", failure.Error.Message);
    }

    [Fact]
    public async Task A_non_string_target_argument_is_an_invalid_argument()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            TransformationArguments.Geometry, geometry,
            TransformationArguments.Target, 4326);

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("'target'", failure.Error.Message);
    }

    [Fact]
    public async Task Coordinates_outside_the_valid_area_are_an_actionable_error()
    {
        // Latitude 95 lies beyond the poles; the Web Mercator formula maps it
        // to a non-finite northing, which the adapter reports as an invalid
        // argument rather than returning poisoned geometry.
        var geometry = GeometryFactory.CreatePoint(0, 95, CoordinateReference.Epsg(4326));

        var result = await TransformAsync(
            TransformationArguments.Geometry, geometry,
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:3857");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("valid area", failure.Error.Message);
    }

    [Fact]
    public async Task Transforming_without_a_geometry_is_an_invalid_argument()
    {
        var result = await TransformAsync(
            TransformationArguments.Source, "EPSG:4326",
            TransformationArguments.Target, "EPSG:3857");

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Contains("'geometry'", failure.Error.Message);
    }

    [Fact]
    public async Task A_pre_cancelled_invocation_is_a_cancelled_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));

        var result = await InvokeAsync(
            TransformContract.Id,
            Arguments(
                TransformationArguments.Geometry, geometry,
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:3857"),
            cancellation.Token);

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.Cancelled, failure.Error.Kind);
        Assert.Equal("operation.cancelled", failure.Error.Code);
    }
}
