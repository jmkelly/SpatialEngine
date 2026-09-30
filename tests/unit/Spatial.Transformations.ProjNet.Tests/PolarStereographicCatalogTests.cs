using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The six polar stereographic variant B grids in the catalogue
/// (SpatialEngine-n58), which the reader has measured against PROJ 9.8.1
/// (ADR-0153) but which no catalogue row carried, so a caller could not name
/// any of them to the transform service.
/// <para>
/// The control points here are PROJ 9.8.1's own for each EPSG code (through
/// pyproj, always_xy, forward, with no datum shift, so what is compared is the
/// projection and nothing else). Every definition is on WGS 84, so the pair
/// under test is WGS 84 in and WGS 84's own projected base out, and the
/// tolerance is the agreement the numbers actually keep rather than a loose
/// bound.
/// </para>
/// </summary>
public sealed class PolarStereographicCatalogTests
{
    /// <summary>
    /// PROJ 9.8.1's coordinates for the six variant B polar grids, forward:
    /// the two Antarctic pairs (71°S on Greenwich and on 70°E with 6,000 km of
    /// false offset), the two NSIDC sea ice grids (70°N on 45°W, 70°S on
    /// Greenwich) and the two remaining Arctic grids (71°N and 75°N). Each is
    /// measured on its own central meridian, off it, and near its pole.
    /// </summary>
    public static TheoryData<int, double, double, double, double> ControlPoints()
    {
        var data = new TheoryData<int, double, double, double, double>();

        // EPSG:3031, WGS 84 / Antarctic Polar Stereographic (lat_ts -71, lon 0).
        data.Add(3031, 0.0, -80.0, 0.0, 1089179.4556261837);
        data.Add(3031, 30.0, -75.0, 819391.6192036181, 1419227.9157567972);
        data.Add(3031, -45.0, -72.0, -1393947.5396750527, 1393947.5396750532);
        data.Add(3031, 179.0, -85.0, 9487.011175154154, -543510.5062151697);

        // EPSG:3032, WGS 84 / Australian Antarctic Polar Stereographic — the
        // same standard parallel on 70°E with 6,000 km of false offset.
        data.Add(3032, 70.0, -80.0, 6000000.0, 7089179.455626184);
        data.Add(3032, 120.0, -75.0, 7255380.793258387, 7053389.560610154);
        data.Add(3032, 150.0, -72.0, 7941390.439023555, 6342319.514489304);
        data.Add(3032, 0.0, -85.0, 5489189.389059778, 6185919.857729575);

        // EPSG:3413, WGS 84 / NSIDC Sea Ice Polar Stereographic North
        // (lat_ts 70, lon -45).
        data.Add(3413, -45.0, 80.0, 0.0, -1085920.2973930992);
        data.Add(3413, -15.0, 75.0, 816939.7487353927, -1414981.1515322526);
        data.Add(3413, -60.0, 72.0, -508693.4760589377, -1898469.8981307785);
        data.Add(3413, -45.0, 89.0, 0.0, -108329.9596389848);

        // EPSG:3976, WGS 84 / NSIDC Sea Ice Polar Stereographic South
        // (lat_ts -70) — 3413's southern twin, on the prime meridian.
        data.Add(3976, 0.0, -80.0, 0.0, 1085920.2973930992);
        data.Add(3976, 30.0, -75.0, 816939.7487353925, 1414981.1515322526);
        data.Add(3976, -45.0, -72.0, -1389776.4220718404, 1389776.4220718406);
        data.Add(3976, 179.0, -85.0, 9458.623134579982, -541884.156459903);

        // EPSG:3995, WGS 84 / Arctic Polar Stereographic (lat_ts 71) — the
        // 3031 pair at the other pole, which is the mirror of 3031's
        // coordinates and so pins the sign of the derived scale factor.
        data.Add(3995, 0.0, 80.0, 0.0, -1089179.4556261837);
        data.Add(3995, 30.0, 75.0, 819391.6192036181, -1419227.9157567972);
        data.Add(3995, -45.0, 72.0, -1393947.5396750527, -1393947.5396750532);
        data.Add(3995, 179.0, 85.0, 9487.011175154154, 543510.5062151697);

        // EPSG:3996, WGS 84 / IBCAO Polar Stereographic (lat_ts 75) — the
        // remaining variant B grid, the one the bead's pair did not cover.
        data.Add(3996, 0.0, 80.0, 0.0, -1100597.5559931227);
        data.Add(3996, 30.0, 75.0, 827981.4761822522, -1434105.9844735416);
        data.Add(3996, -45.0, 72.0, -1408560.5888214004, -1408560.588821401);
        data.Add(3996, 179.0, 85.0, 9586.465535242047, 549208.2426886169);

        return data;
    }

    /// <summary>
    /// The six grids are nameable to the catalogue and land where PROJ puts
    /// them. Before this bead every one of these was refused by name — the
    /// method was reachable only through the reader's own tests.
    /// </summary>
    [Theory]
    [MemberData(nameof(ControlPoints))]
    public async Task A_polar_stereographic_variant_B_grid_transforms_where_PROJ_puts_it(
        int code, double longitude, double latitude, double easting, double northing)
    {
        var coordinate = (await Project(longitude, latitude, code)).Coordinate!.Value;

        // A millimetre: the tolerance is the agreement the numbers keep (the
        // largest deviation measured across the points above is under a
        // micrometre), not a loose bound.
        Assert.InRange(Math.Abs(coordinate.X - easting), 0.0, 0.001);
        Assert.InRange(Math.Abs(coordinate.Y - northing), 0.0, 0.001);
    }

    /// <summary>
    /// Each grid describes itself: the EPSG name, the polar stereographic
    /// variant B method, and the WGS 84 base every one of the six shares — so
    /// the reader turned the standard parallel into a variant A pole and scale
    /// factor rather than handing on a parameter the projection cannot read.
    /// </summary>
    [Theory]
    [InlineData(3031, "WGS 84 / Antarctic Polar Stereographic")]
    [InlineData(3032, "WGS 84 / Australian Antarctic Polar Stereographic")]
    [InlineData(3413, "WGS 84 / NSIDC Sea Ice Polar Stereographic North")]
    [InlineData(3976, "WGS 84 / NSIDC Sea Ice Polar Stereographic South")]
    [InlineData(3995, "WGS 84 / Arctic Polar Stereographic")]
    [InlineData(3996, "WGS 84 / IBCAO Polar Stereographic")]
    public async Task A_polar_stereographic_variant_B_grid_describes_its_method_and_its_datum(int code, string name)
    {
        var description = await DescribeAsync("crs", $"EPSG:{code}");

        Assert.Equal(name, description.Name);
        Assert.Equal(CrsKind.Projected, description.Kind);
        Assert.Equal("World Geodetic System 1984", description.Datum);
        Assert.Equal("WGS 84", description.Ellipsoid!.Name);
        Assert.Equal("Polar_Stereographic", Assert.IsType<ProjectedDefinition>(ProjEpsgCatalog.DefinitionOf(code)).ProjectionClass);
    }

    /// <summary>
    /// The standard parallel is not one of the parameters the built
    /// projection receives: the reader spent it on the pole and the scale
    /// factor, and a definition that still carried it would be a definition
    /// the projection silently ignores.
    /// </summary>
    [Theory]
    [InlineData(3031, -90.0)]
    [InlineData(3032, -90.0)]
    [InlineData(3413, 90.0)]
    [InlineData(3976, -90.0)]
    [InlineData(3995, 90.0)]
    [InlineData(3996, 90.0)]
    public void A_polar_stereographic_variant_B_grid_is_read_as_a_pole_and_a_scale_factor(int code, double pole)
    {
        var parameters = Assert.IsType<ProjectedDefinition>(ProjEpsgCatalog.DefinitionOf(code)).Parameters;

        Assert.Equal(pole, parameters.Single(parameter => parameter.Name == "latitude_of_origin").Value);
        Assert.Contains(parameters, parameter => parameter.Name == "scale_factor");
        Assert.DoesNotContain(parameters, parameter => parameter.Name == "latitude_of_standard_parallel");
    }

    /// <summary>
    /// A grid inverts back to the coordinates it was given, on both sides of
    /// the pole.
    /// </summary>
    [Theory]
    [InlineData(3031, 30.0, -75.0)]
    [InlineData(3976, 30.0, -75.0)]
    [InlineData(3413, -15.0, 75.0)]
    [InlineData(3996, 179.0, 85.0)]
    public async Task A_polar_stereographic_variant_B_grid_inverts_back_to_WGS84(int code, double longitude, double latitude)
    {
        var projected = (await Project(longitude, latitude, code)).Coordinate!.Value;
        var round = await TransformAsync(
            "geometry", new Point(projected, CoordinateReference.Epsg(code)),
            "source", $"EPSG:{code}",
            "target", "EPSG:4326");

        var back = Assert.IsType<Point>(round).Coordinate!.Value;

        Assert.Equal(longitude, back.X, 6);
        Assert.Equal(latitude, back.Y, 6);
    }

    private static async Task<Point> Project(double longitude, double latitude, int code)
    {
        var result = await TransformAsync(
            "geometry", GeometryFactory.CreatePoint(longitude, latitude, CoordinateReference.Epsg(4326)),
            "source", "EPSG:4326",
            "target", $"EPSG:{code}");

        return Assert.IsType<Point>(result);
    }
}
