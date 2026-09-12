using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Transformations;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Control-point verification of the transformation adapter (ADR-0027): known
/// real-world coordinates transformed between
/// catalogue CRSs must land on authoritative values from an independent
/// reference implementation (PROJ 9 via pyproj, always_xy). Pairs without a
/// datum shift (WGS84/ETRS89/NAD83 families) are asserted to centimetre
/// tightness; OSGB36 uses the classic Helmert approximation embedded in the
/// catalogue (no grid support in ProjNet), asserted within 0.1 m of the
/// official OSTN15-based value — see ADR-0027 §accuracy.
/// </summary>
public sealed class ProjNetControlPointTests
{
    [Fact]
    public async Task Berlin_WGS84_to_UTM_32N_matches_the_reference_within_a_centimetre()
    {
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.Berlin.Lon, ControlPoints.Berlin.Lat,
            CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(geometry, "EPSG:4326", "EPSG:32632");
        AssertClose(x, ControlPoints.BerlinUtm32.X, 0.01);
        AssertClose(y, ControlPoints.BerlinUtm32.Y, 0.01);
    }

    [Fact]
    public async Task London_WGS84_to_Web_Mercator_matches_the_reference()
    {
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.London.Lon, ControlPoints.London.Lat,
            CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(geometry, "EPSG:4326", "EPSG:3857");
        AssertClose(x, ControlPoints.LondonWebMercator.X, 0.01);
        AssertClose(y, ControlPoints.LondonWebMercator.Y, 0.01);
    }

    [Fact]
    public async Task Munich_ETRS89_to_ETRS89_UTM_32N_matches_the_reference()
    {
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.Munich.Lon, ControlPoints.Munich.Lat,
            CoordinateReference.Epsg(4258));

        var (x, y) = await Transform(geometry, "EPSG:4258", "EPSG:25832");
        AssertClose(x, ControlPoints.MunichEtrsUtm32.X, 0.01);
        AssertClose(y, ControlPoints.MunichEtrsUtm32.Y, 0.01);
    }

    [Fact]
    public async Task Lyon_WGS84_to_Lambert_93_matches_the_reference()
    {
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.Lyon.Lon, ControlPoints.Lyon.Lat,
            CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(geometry, "EPSG:4326", "EPSG:2154");
        AssertClose(x, ControlPoints.LyonLambert93.X, 0.01);
        AssertClose(y, ControlPoints.LyonLambert93.Y, 0.01);
    }

    [Fact]
    public async Task Seattle_NAD83_to_NAD83_UTM_10N_matches_the_reference()
    {
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.Seattle.Lon, ControlPoints.Seattle.Lat,
            CoordinateReference.Epsg(4269));

        var (x, y) = await Transform(geometry, "EPSG:4269", "EPSG:26910");
        AssertClose(x, ControlPoints.SeattleNad83Utm10.X, 0.01);
        AssertClose(y, ControlPoints.SeattleNad83Utm10.Y, 0.01);
    }

    [Fact]
    public async Task London_WGS84_to_British_National_Grid_is_within_the_Helmert_tolerance()
    {
        // The catalogue shifts OSGB36 to WGS84 with the classic Helmert
        // approximation; PROJ's official path uses the OSTN15 grid. The two
        // agree to millimetres at this control point; the 0.1 m tolerance
        // documents the approximation's accuracy ceiling (ADR-0027 §accuracy).
        var geometry = GeometryFactory.CreatePoint(
            ControlPoints.London.Lon, ControlPoints.London.Lat,
            CoordinateReference.Epsg(4326));

        var (x, y) = await Transform(geometry, "EPSG:4326", "EPSG:27700");
        AssertClose(x, ControlPoints.LondonBritishNationalGrid.X, 0.1);
        AssertClose(y, ControlPoints.LondonBritishNationalGrid.Y, 0.1);
    }

    [Fact]
    public async Task The_UTM_control_point_inverts_back_to_WGS84()
    {
        var (easting, northing) = ControlPoints.BerlinUtm32;
        var geometry = GeometryFactory.CreatePoint(easting, northing, CoordinateReference.Epsg(32632));

        var (lon, lat) = await Transform(geometry, "EPSG:32632", "EPSG:4326");
        AssertClose(lon, ControlPoints.Berlin.Lon, 0.000001);
        AssertClose(lat, ControlPoints.Berlin.Lat, 0.000001);
    }

    private static async Task<(double X, double Y)> Transform(IGeometry geometry, string source, string target)
    {
        var transformed = await TransformAsync(
            "geometry", geometry,
            "source", source,
            "target", target);
        var point = Assert.IsType<Point>(transformed);
        Assert.Equal(new CoordinateReference("EPSG", target[(target.IndexOf(':') + 1)..]), transformed.CoordinateReference);
        return (point.X!.Value, point.Y!.Value);
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}.");
    }
}
