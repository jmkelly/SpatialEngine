using Spatial.Contracts;
using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The transform verb's own control points for a deployed datum-shift grid
/// (ADR-0107). ADR-0105 published the grid as a candidate and said in every
/// method string that the verb did not yet apply it; this is the slice that
/// makes that clause untrue.
/// <list type="bullet">
/// <item>over ground the grid covers, the point is shifted by the grid;</item>
/// <item>over ground it does not, the point is shifted by the classic Helmert
/// — per coordinate, in the same request, so a geometry crossing the edge of
/// the block is answered by both;</item>
/// <item>the Helmert fallback still lands the London control point inside the
/// 0.1 m it has always been stated at, and the grid's own round trip
/// closes;</item>
/// <item>with no bundle deployed, nothing changes at all.</item>
/// </list>
/// <para>
/// The grid is the synthetic constant-shift one from
/// <see cref="DeployedGrid"/>, so the expected answer is arithmetic rather
/// than a remembered number, and no published bundle is redistributed
/// (ADR-0105 §licence). The Helmert numbers compared against PROJ are
/// PROJ 9.8.1's, generated from the same seven parameters the catalogue
/// carries.
/// </para>
/// </summary>
public sealed class GridShiftTransformTests : IDisposable
{
    /// <summary>The fixture's constant shift, in seconds of arc.</summary>
    private const double LatitudeSeconds = 1.0;

    private const double LongitudeSeconds = 2.0;

    /// <summary>
    /// A block over northern Scotland: nowhere near London, so London is
    /// outside it and the classic Helmert answers there.
    /// </summary>
    private const double FarSouth = 56.0;
    private const double FarNorth = 59.0;
    private const double FarWest = -8.0;
    private const double FarEast = -2.0;

    private readonly DeployedGrid _deployment = new();

    public void Dispose() => _deployment.Dispose();

    [Fact]
    public void Over_the_ground_the_grid_covers_the_point_is_shifted_by_the_grid()
    {
        // London is inside this block, and the grid shifts OSGB 36 to WGS 84 by
        // a constant the test chose. The answer is therefore the input less that
        // shift, and nothing else: the Helmert is nowhere in it.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var bare = new ProjNetTransforms();

        var shifted = Transform(deployed, "EPSG:4326", "EPSG:4277");
        var expected = (
            Longitude: ControlPoints.London.Lon - (LongitudeSeconds / 3600.0),
            Latitude: ControlPoints.London.Lat - (LatitudeSeconds / 3600.0));

        Assert.Equal(expected.Longitude, shifted.X!.Value, 9);
        Assert.Equal(expected.Latitude, shifted.Y!.Value, 9);

        // And it is emphatically not the Helmert answer the same host would
        // give with nothing deployed, which is the whole of the change.
        var helmert = Transform(bare, "EPSG:4326", "EPSG:4277");
        Assert.NotEqual(helmert.X!.Value, shifted.X!.Value, 6);
    }

    [Fact]
    public void Outside_the_block_the_point_is_shifted_by_the_Helmert()
    {
        // The block is over northern Scotland, so London is outside it and the
        // classic Helmert stands — bit for bit the answer a host with no bundle
        // gives, because the fallback is the operation, not an approximation of
        // one.
        var deployed = new ProjNetTransforms([_deployment.Constant(FarSouth, FarNorth, FarWest, FarEast)]);
        var bare = new ProjNetTransforms();

        var fallback = Transform(deployed, "EPSG:4326", "EPSG:4277");
        var expected = Transform(bare, "EPSG:4326", "EPSG:4277");

        Assert.Equal(expected.X!.Value, fallback.X!.Value, 12);
        Assert.Equal(expected.Y!.Value, fallback.Y!.Value, 12);
    }

    [Fact]
    public void The_London_control_point_still_holds_on_the_fallback_path()
    {
        // ADR-0105 §fallback: the fallback is not a consolation prize, it is the
        // operation the engine states an accuracy for. Projected to the national
        // grid from outside the block, it must still land on the PROJ reference
        // inside the 0.1 m the catalogue's Helmert path is stated at.
        var deployed = new ProjNetTransforms([_deployment.Constant(FarSouth, FarNorth, FarWest, FarEast)]);
        var projected = Transform(deployed, "EPSG:4326", "EPSG:27700");

        var (expectedX, expectedY) = ControlPoints.LondonBritishNationalGrid;
        var residual = Math.Sqrt(
            ((projected.X!.Value - expectedX) * (projected.X!.Value - expectedX))
            + ((projected.Y!.Value - expectedY) * (projected.Y!.Value - expectedY)));

        Assert.True(
            residual <= 0.1,
            $"the fallback path lands {residual:F4} m from the PROJ 9 reference, beyond the 0.1 m the "
            + $"catalogue's Helmert path is stated at (ADR-0027 §accuracy).");
    }

    [Fact]
    public void A_geometry_straddling_the_edge_of_the_block_is_shifted_by_both()
    {
        // A grid is a fact about the ground and a geometry is under no
        // obligation to stay inside one, so the choice is per coordinate and
        // not per request: one MultiPoint, one point the grid covers and one
        // it does not, each answered by whichever operation reaches it.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var bare = new ProjNetTransforms();

        var inside = (ControlPoints.London.Lon, ControlPoints.London.Lat);
        var outside = (3.0, 51.0);
        var geometry = GeometryFactory.CreateMultiPoint(
            [
                GeometryFactory.CreatePoint(inside.Item1, inside.Item2, CoordinateReference.Epsg(4326)),
                GeometryFactory.CreatePoint(outside.Item1, outside.Item2, CoordinateReference.Epsg(4326)),
            ],
            CoordinateReference.Epsg(4326));

        var shifted = Assert.IsType<MultiPoint>(deployed.Transform(geometry, "EPSG:4326", "EPSG:4277"));
        var reference = Assert.IsType<MultiPoint>(bare.Transform(geometry, "EPSG:4326", "EPSG:4277"));

        // The first point moved by the grid's constant and by nothing else.
        Assert.Equal(inside.Item1 - (LongitudeSeconds / 3600.0), shifted.Points[0].X!.Value, 9);
        Assert.Equal(inside.Item2 - (LatitudeSeconds / 3600.0), shifted.Points[0].Y!.Value, 9);

        // The second is off the block, so it is the Helmert answer, unchanged.
        Assert.Equal(reference.Points[1].X!.Value, shifted.Points[1].X!.Value, 12);
        Assert.Equal(reference.Points[1].Y!.Value, shifted.Points[1].Y!.Value, 12);
    }

    [Fact]
    public void A_grid_shifted_point_returns_to_where_it_started()
    {
        // A tabulated shift is not analytically invertible, so a forward and an
        // inverse that disagreed would report a round trip that did not close.
        // Both directions are the verb's own, through the same deployed bundle.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);

        var toOsgb = Transform(deployed, "EPSG:4326", "EPSG:4277");
        var back = deployed.Transform(
            GeometryFactory.CreatePoint(toOsgb.X!.Value, toOsgb.Y!.Value, CoordinateReference.Epsg(4277)),
            "EPSG:4277",
            "EPSG:4326");
        var returned = Assert.IsType<Point>(back);

        Assert.Equal(ControlPoints.London.Lon, returned.X!.Value, 9);
        Assert.Equal(ControlPoints.London.Lat, returned.Y!.Value, 9);
    }

    [Fact]
    public void A_deployment_leaves_a_datum_no_bundle_serves_untouched()
    {
        // RGF93 is a modern datum the catalogue carries with no shift of its
        // own, so an Ordnance Survey bundle must not move a French point.
        var withBundle = new ProjNetTransforms([_deployment.Constant()]);
        var withoutBundle = new ProjNetTransforms();

        var (a, b) = (withBundle, withoutBundle);
        var moved = Transform(a, "EPSG:4326", "EPSG:2154", ControlPoints.Lyon.Lon, ControlPoints.Lyon.Lat);
        var untouched = Transform(b, "EPSG:4326", "EPSG:2154", ControlPoints.Lyon.Lon, ControlPoints.Lyon.Lat);

        Assert.Equal(untouched.X!.Value, moved.X!.Value, 12);
        Assert.Equal(untouched.Y!.Value, moved.Y!.Value, 12);
    }

    [Fact]
    public void A_deployment_does_not_bypass_the_contract_s_validation()
    {
        // Taking the transform off ProjNet's composition must not take the
        // validation with it: an identity the catalogue does not serve is
        // still a bad argument, and says so with the same structured code.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var geometry = GeometryFactory.CreatePoint(-0.1276, 51.5072, CoordinateReference.Epsg(4326));

        var failure = Assert.Throws<SpatialException>(
            () => deployed.Transform(geometry, "EPSG:4326", "EPSG:999999"));

        Assert.Equal("invalid.arguments", failure.Code);
    }

    [Fact]
    public void A_cancelled_transform_stops_before_it_shifts_anything()
    {
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var geometry = GeometryFactory.CreatePoint(-0.1276, 51.5072, CoordinateReference.Epsg(4326));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => deployed.Transform(geometry, "EPSG:4326", "EPSG:4277", cancelled.Token));
    }

    [Fact]
    public void A_cancellation_is_honoured_part_way_through_a_large_geometry()
    {
        // The grid-backed walk is per coordinate, so a large geometry is a long
        // operation and the token is checked inside the walk as well as at its
        // head. The line is long enough that a cancellation landing a
        // millisecond in cannot plausibly be the last thing that happens.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var coordinates = new Coordinate[100_000];
        for (var index = 0; index < coordinates.Length; index++)
        {
            coordinates[index] = new Coordinate(-9.0 + (index * 0.0005), 51.0);
        }

        var line = GeometryFactory.CreateLineString(coordinates, CoordinateReference.Epsg(4326));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        Assert.ThrowsAny<OperationCanceledException>(
            () => deployed.Transform(line, "EPSG:4326", "EPSG:4277", cancellation.Token));
    }

    [Fact]
    public void The_grid_path_preserves_the_ordinates_and_the_layout()
    {
        // The grid path is a different walk over the geometry, not a different
        // geometry: a coordinate with a height keeps it, and so does its layout.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var geometry = GeometryFactory.CreatePoint(
            new Coordinate(ControlPoints.London.Lon, ControlPoints.London.Lat, 123.5),
            CoordinateReference.Epsg(4326));

        var point = Assert.IsType<Point>(deployed.Transform(geometry, "EPSG:4326", "EPSG:4277"));

        Assert.Equal(123.5, point.Coordinate!.Value.Z);
    }

    [Fact]
    public void A_deployed_grid_does_not_change_what_the_listing_says_about_it()
    {
        // The transform verb applying the grid is not the same fact as the grid
        // being deployed, and the listing publishes the first while the engine
        // applies the second. They have to agree, which is what this pins.
        var deployed = new ProjNetTransforms([_deployment.Constant()]);
        var candidates = deployed.FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"));

        var grid = candidates[0];
        Assert.Contains(grid.Steps, step => step.GridShift is not null);
        Assert.DoesNotContain("still applies the classic Helmert", grid.Method, StringComparison.Ordinal);
    }

    private static Point Transform(
        ProjNetTransforms service,
        string source,
        string target,
        double? longitude = null,
        double? latitude = null)
    {
        var geometry = GeometryFactory.CreatePoint(
            longitude ?? ControlPoints.London.Lon,
            latitude ?? ControlPoints.London.Lat,
            CoordinateReference.Epsg(4326));
        return Assert.IsType<Point>(service.Transform(geometry, source, target));
    }
}
