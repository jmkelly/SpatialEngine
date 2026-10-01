using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet.Grids;
using static Spatial.Transformations.ProjNet.Tests.GraphInvoker;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The NADCON row the catalogue publishes for NAD27, and what a deployed pair
/// does to the answer (ADR-0180).
/// <para>
/// NADCON is registered by EPSG for one operation and not for the pivot: NAD27
/// to NAD83, by the latitude/longitude difference files <c>conus.las</c> and
/// <c>conus.los</c> (EPSG:1241), at 0.15 m. A row in a table keyed on a datum
/// reaching WGS 84 cannot hold that, so the row names the datum it reaches and
/// the graph continues from there along that datum's own registered path — the
/// null operation ADR-0163 publishes for NAD83 at 4.0 m.
/// </para>
/// <para>
/// The bundle is the repository's own fixture, deployed the way an operator
/// would deploy the published one: no published grid is vendored or fetched
/// (ADR-0105 §licence), so the expected answer is the arithmetic the test
/// writes down rather than a remembered number. Whether a pair NOAA published
/// reads the way this fixture does is SpatialEngine-yt2's exercise.
/// </para>
/// </summary>
public sealed class NadconGridOperationTests : IDisposable
{
    /// <summary>The EPSG code of the datum NAD27's own Helmert row is about.</summary>
    private const int Nad27DatumCode = 6267;

    /// <summary>The block the fixture pair covers: a degree of central Texas,
    /// inside the CONUS extent EPSG registers operation 1241 over.</summary>
    private const double South = 30.0;
    private const double North = 31.0;
    private const double West = -100.0;
    private const double East = -99.0;

    private const double LatitudeSeconds = 1.0f;
    private const double LongitudeSeconds = 2.0f;

    /// <summary>A point inside the deployed block, and one well outside it.</summary>
    private const double InsideLongitude = -99.5;
    private const double InsideLatitude = 30.5;
    private const double OutsideLongitude = -122.33;
    private const double OutsideLatitude = 47.61;

    private readonly string _directory;

    public NadconGridOperationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"spatialengine-nadcon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(
            Path.Combine(_directory, "conus.las"),
            NadconFixture.ToBytes(
                NadconFixture.Constant("CONUS", South, North, West, East, 0.25, 0.25, (float)LatitudeSeconds),
                longitude: false));
        File.WriteAllBytes(
            Path.Combine(_directory, "conus.los"),
            NadconFixture.ToBytes(
                NadconFixture.Constant("CONUS", South, North, West, East, 0.25, 0.25, (float)LongitudeSeconds),
                longitude: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DatumShiftGridRegistry Registry() => DatumShiftGridRegistry.Load([_directory]);

    [Fact]
    public void The_catalogue_names_the_pair_EPSG_registers_for_NAD27()
    {
        // EPSG:1241 "NAD27 to NAD83 (1)" is the NADCON operation, and the two
        // grid files it names are the whole of what a row is for: a file name
        // an operator can go and deploy. The accuracy is the one the same
        // record states, because a NADCON shift record states none of its own
        // (ADR-0168 §4).
        var row = Assert.Single(EpsgGridShiftOperations.For("NAD27"));

        Assert.Equal("conus.las", row.FileName);
        Assert.Equal("conus.los", row.LongitudeFileName);
        Assert.Equal(0.15, row.AccuracyMetres);
    }

    [Fact]
    public void A_NADCON_row_names_the_datum_it_reaches_rather_than_the_pivot()
    {
        // The bead's first question, and the one the shape turns on: NADCON is
        // registered for NAD27 to NAD83, so a row that claimed the pair reached
        // WGS 84 would publish a claim no EPSG record supports. Every other row
        // in the table does reach the pivot, and says so.
        Assert.Equal(4269, Assert.Single(EpsgGridShiftOperations.For("NAD27")).TargetDatumCode);
        Assert.All(
            EpsgGridShiftOperations.Bundles.Where(operation => operation.GraphName != "NAD27"),
            operation => Assert.Equal(4326, operation.TargetDatumCode));
    }

    [Fact]
    public void NAD27_is_a_served_datum_carrying_the_Helmert_EPSG_registers_over_CONUS()
    {
        // A row in the grid table is not a node: the graph has no NAD27 to join
        // it to unless the catalogue carries the datum itself. Its registered
        // grid-free path is EPSG:1173 "NAD27 to WGS 84 (4)" — three
        // translations over the USA - CONUS - onshore, at 10.0 m — and that is
        // the operation the accuracy and the bounds are read from.
        var operation = EpsgDatumOperations.ReadForTest("North American Datum 1927");

        Assert.NotNull(operation);
        Assert.Equal(Nad27DatumCode, operation!.DatumCode);
        Assert.Equal(10.0, operation.AccuracyMetres);
        Assert.Equal(1173, operation.OperationCode);
        Assert.Contains(operation.AreaOfUseName, operation.ExtentName, StringComparison.Ordinal);

        // The parameters live in the vendored WKT and nowhere else (ADR-0086).
        Assert.True(
            ProjEpsgCatalog.TryGetDatum(4267, out var node),
            "NAD27 is in the vendored catalogue, so the graph has a node for a grid row to join to.");
        Assert.Equal("NAD27", node!.Code);
        var (tx, ty, tz, rx, ry, rz, scale) = node.ToWgs84;
        Assert.Equal([-8.0, 160.0, 176.0, 0.0, 0.0, 0.0, 0.0], new[] { tx, ty, tz, rx, ry, rz, scale });
    }

    [Fact]
    public void A_deployed_pair_publishes_a_grid_leg_to_NAD83_and_the_null_operation_behind_it()
    {
        // The published path says what was applied and what it cost. The grid
        // leg names the datum the file reaches — NAD83 — and never the pivot,
        // and behind it stands the operation EPSG registers for NAD83 against
        // WGS 84, which moves nothing and is worth 4.0 m (ADR-0163). A row
        // that reached WGS 84 directly would either publish a claim no EPSG
        // record supports (1241 registers NAD27 to NAD83) or publish EPSG's
        // own pivot-registered restatement of the pair (15851, 5.0 m), which
        // reaches the pivot by assuming the leg behind it is free.
        var candidate = Assert.Single(
            Search("EPSG:4267", "EPSG:4326", Registry()),
            candidate => candidate.Steps.Any(step => step.GridShift is not null));

        var step = Assert.Single(candidate.Steps, step => step.GridShift is not null);
        Assert.Equal("NAD83_To_NAD27_CONUS", step.Name);
        Assert.Equal("NADCON", step.GridShift!.Format);
        Assert.Equal("conus.las", step.GridShift.FileName);
        Assert.Contains("NADCON", candidate.Method, StringComparison.Ordinal);

        var nullLeg = Assert.Single(candidate.Steps, step => step.Name == "NAD83_To_WGS84_Geocentric_Translation");
        var (tx, ty, tz, rx, ry, rz, scale) = Assert.IsType<HelmertParameters>(nullLeg.Parameters);
        Assert.Equal([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], new[] { tx, ty, tz, rx, ry, rz, scale });
    }

    [Fact]
    public void The_published_accuracy_is_the_pair_and_the_null_operation_in_quadrature()
    {
        // 0.15 m from EPSG:1241 and 4.0 m from EPSG:1188, combined the way
        // ADR-0087 combines two legs. Neither figure is typed in here: the
        // grid's is the row's and the null leg's is the datum's, so a catalogue
        // that restated either would be caught by this.
        var candidate = Assert.Single(
            Search("EPSG:4267", "EPSG:4326", Registry()),
            candidate => candidate.Steps.Any(step => step.GridShift is not null));

        var expected = Math.Sqrt((0.15 * 0.15) + (4.0 * 4.0));
        Assert.Equal(expected, candidate.AccuracyMetres, 9);
        Assert.False(candidate.Approximate, "no leg of this path is a Helmert approximation.");
    }

    [Fact]
    public void Over_the_block_the_transform_applies_the_pair()
    {
        // The point is shifted by the fixture's constant and then by the
        // registered null operation, which is the leg behind the grid and is
        // applied exactly as a host with no bundle applies it: ADR-0163 records
        // that this round trip moves a point by millimetres rather than by
        // nothing, far inside the 4.0 m it publishes.
        var shifted = Transform(new ProjNetTransforms(Registry()), 4267, 4326, InsideLongitude, InsideLatitude);
        var (gridLongitude, gridLatitude) = (
            InsideLongitude + (LongitudeSeconds / 3600.0),
            InsideLatitude + (LatitudeSeconds / 3600.0));
        var behind = Transform(new ProjNetTransforms(), 4269, 4326, gridLongitude, gridLatitude);

        Assert.Equal(behind.X!.Value, shifted.X!.Value, 9);
        Assert.Equal(behind.Y!.Value, shifted.Y!.Value, 9);
        Assert.True(
            Math.Abs(behind.Y!.Value - gridLatitude) < 0.001,
            "the registered null operation moves the point by millimetres, not by the metres a datum shift would.");
    }

    [Fact]
    public void Outside_the_block_the_transform_is_the_Helmert_a_bare_host_gives()
    {
        var deployed = Transform(new ProjNetTransforms(Registry()), 4267, 4326, OutsideLongitude, OutsideLatitude);
        var bare = Transform(new ProjNetTransforms(), 4267, 4326, OutsideLongitude, OutsideLatitude);

        Assert.Equal(bare.X!.Value, deployed.X!.Value, 12);
        Assert.Equal(bare.Y!.Value, deployed.Y!.Value, 12);
    }

    [Fact]
    public void With_no_pair_deployed_the_search_is_the_one_it_was()
    {
        // The Helmert path still stands and says so; the catalogue naming a
        // bundle is not the catalogue deploying one (ADR-0105).
        var candidates = Search("EPSG:4267", "EPSG:4326", DatumShiftGridRegistry.Empty);

        Assert.All(candidates, candidate => Assert.DoesNotContain(candidate.Steps, step => step.GridShift is not null));
    }

    private static Point Transform(ProjNetTransforms transforms, int source, int target, double longitude, double latitude) =>
        Assert.IsType<Point>(transforms.Transform(
            GeometryFactory.CreatePoint(longitude, latitude, CoordinateReference.Epsg(source)),
            $"EPSG:{source}",
            $"EPSG:{target}"));
}
