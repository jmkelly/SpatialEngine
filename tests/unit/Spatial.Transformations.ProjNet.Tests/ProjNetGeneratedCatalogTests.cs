using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The generated CRS families of the ProjNet provider (ADR-0027): UTM zones
/// 1-60 north and south (EPSG 32601-32660 / 32701-32760) and their
/// datum-template siblings (ETRS89 zones 28-38, NAD83 zones 1-23) are
/// built from one parameter template instead of being enumerated by hand, so
/// a client in any zone gets a served CRS rather than a catalogue rejection.
/// <para>
/// Two things are pinned here. The reproduction is that every zone
/// describes and transforms — before the families existed, seven UTM zones
/// were listed by hand and every other zone threw
/// <c>invalid.arguments</c>. The regression side is that generation is
/// indistinguishable from the hand-written rows: the fifteen pre-existing
/// codes are pinned to the byte-exact coordinates they produced before, and
/// the seven hand-written UTM rows among them now come from the template.
/// </para>
/// </summary>
public sealed class ProjNetGeneratedCatalogTests
{
    /// <summary>Berlin, the catalogue's canonical control point (PROJ 9, always_xy).</summary>
    private const double BerlinLongitude = 13.405;

    private const double BerlinLatitude = 52.52;

    /// <summary>
    /// The UTM zones, both hemispheres: EPSG 32601-32660 north and
    /// 32701-32760 south, over the 6-degree wide bands whose central
    /// meridian is <c>6 * zone - 183</c>.
    /// </summary>
    public static TheoryData<int, int, int> UtmZones()
    {
        var data = new TheoryData<int, int, int>();
        for (var zone = 1; zone <= 60; zone++)
        {
            data.Add(zone, 32600 + zone, 32700 + zone);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UtmZones))]
    public async Task Every_UTM_zone_serves_its_projected_CRS(int zone, int north, int south)
    {
        foreach (var (code, hemisphere) in new[] { (north, "N"), (south, "S") })
        {
            var description = await DescribeAsync("crs", $"EPSG:{code}");

            Assert.Equal($"WGS 84 / UTM zone {zone}{hemisphere}", description.Name);
            Assert.Equal(CrsKind.Projected, description.Kind);
            Assert.Equal(2, description.Dimension);
            Assert.Equal("World Geodetic System 1984", description.Datum);
            Assert.Equal("metre", description.Axes[0].UnitName);
            Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
            Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
        }
    }

    [Theory]
    [MemberData(nameof(UtmZones))]
    public async Task Every_UTM_zone_transforms_from_its_central_meridian(int zone, int north, int south)
    {
        // A point on the zone's own central meridian lands on that meridian,
        // so the easting is the 500,000 m false easting. The northing is the
        // scaled 45-degree arc, and in the southern hemisphere the same arc
        // below the 10,000,000 m false northing, so the two hemisphere
        // northings of one zone add up to it.
        var centralMeridian = 6.0 * zone - 183.0;
        var northern = await Northing(centralMeridian, 45.0, north);
        var southern = await Northing(centralMeridian, -45.0, south);

        Assert.InRange(northern.X, 499_000.0, 501_000.0);
        Assert.InRange(southern.X, 499_000.0, 501_000.0);
        Assert.InRange(northern.Y, 4_900_000.0, 5_100_000.0);
        Assert.InRange(southern.Y, 4_900_000.0, 5_100_000.0);
        Assert.InRange(northern.Y + southern.Y, 9_999_000.0, 10_001_000.0);
        Assert.Equal(CoordinateReference.Epsg(south), (await Project(centralMeridian, -45.0, south)).CoordinateReference);
    }

    [Theory]
    [InlineData(25828, 28)]
    [InlineData(25831, 31)]
    [InlineData(25838, 38)]
    [InlineData(26901, 1)]
    [InlineData(26912, 12)]
    [InlineData(26923, 23)]
    public async Task The_datum_siblings_of_the_UTM_template_are_served(int code, int zone)
    {
        var description = await DescribeAsync("crs", $"EPSG:{code}");

        Assert.Contains($"UTM zone {zone}N", description.Name, StringComparison.Ordinal);
        Assert.Equal(CrsKind.Projected, description.Kind);
    }

    [Fact]
    public async Task The_pre_existing_entries_keep_their_coordinates_byte_for_byte()
    {
        // The regression pin: the exact doubles these fifteen codes produced
        // before the families existed. A generated entry that drifted by one
        // ulp from the hand-written row it replaced would fail here.
        foreach (var (code, x, y) in PreChange)
        {
            var coordinate = (await Project(BerlinLongitude, BerlinLatitude, code)).Coordinate!.Value;
            Assert.True(x.Equals(coordinate.X), $"EPSG:{code} easting: expected {x:R}, got {coordinate.X:R}");
            Assert.True(y.Equals(coordinate.Y), $"EPSG:{code} northing: expected {y:R}, got {coordinate.Y:R}");
        }
    }

    [Fact]
    public void A_generated_entry_is_built_once_and_built_lazily()
    {
        // A generated zone is a row like any other: one dictionary entry,
        // one lazy build, the same instance on every lookup. Whether a
        // *particular* zone is still unbuilt cannot be observed here — the
        // catalogue is a process-wide singleton shared by the test
        // assembly, and the zone sweep above may already have asked for it —
        // so this pins the caching half, and `IsBuilt` reports the lazy half
        // for any caller that has not yet asked.
        const int zone = 32601;
        Assert.True(ProjEpsgCatalog.TryGet(zone, out var first));
        Assert.True(ProjEpsgCatalog.IsBuilt(zone));
        Assert.True(ProjEpsgCatalog.TryGet(zone, out var second));
        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_code_outside_every_family_is_still_rejected()
    {
        var error = await Assert.ThrowsAsync<SpatialException>(
            () => DescribeAsync("crs", "EPSG:12345"));

        Assert.Equal("invalid.arguments", error.Code);
        Assert.Contains("12345", error.Message);
    }

    [Fact]
    public void A_pre_cancelled_describe_of_a_generated_CRS_stops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new ProjNetTransforms().Describe("EPSG:32601", cancellation.Token));
    }

    /// <summary>
    /// The pre-change output for the fifteen enumerated codes: the exact
    /// coordinates of (13.405, 52.52) in EPSG:4326 projected into each,
    /// captured before the families existed. Seven of these codes (25832,
    /// 25833, 26910, 32610, 32612, 32632, 32633) are now generated from the
    /// UTM template, so this table is also the proof that generation is
    /// indistinguishable from the hand-written rows it replaces.
    /// </summary>
    /// <summary>Projects a point from EPSG:4326 into a UTM zone and reads the coordinates back.</summary>
    private static async Task<Coordinate> Northing(double longitude, double latitude, int code) => (await Project(longitude, latitude, code)).Coordinate!.Value;

    private static async Task<Point> Project(double longitude, double latitude, int code)
    {
        var result = await TransformAsync(
            "geometry", GeometryFactory.CreatePoint(longitude, latitude, CoordinateReference.Epsg(4326)),
            "source", "EPSG:4326",
            "target", $"EPSG:{code}");

        return Assert.IsType<Point>(result);
    }

    private static readonly (int Code, double X, double Y)[] PreChange =
    [
        (2154, 1407596.8588771431, 7316849.164525929),
        (3857, 1492237.774083832, 6894699.801282422),
        (4171, 13.405, 52.520000038676585),
        (4258, 13.405, 52.520000038676585),
        (4269, 13.405, 52.520000038676585),
        (4277, 13.408157853166417, 52.51929992519474),
        (4326, 13.405, 52.52),
        (25832, 798812.8023187008, 5827999.904287369),
        (25833, 391779.25934663735, 5820072.163390562),
        (26910, -4381494.560139428, 6465352.6635185955),
        (27700, 1441992.367098121, 403674.73780773266),
        (32610, -4381494.587747481, 6465352.665446997),
        (32612, 165900.11245769612, 9930716.27676071),
        (32632, 798812.802578396, 5827999.900112441),
        (32633, 391779.2592527044, 5820072.159212841),
    ];
}
