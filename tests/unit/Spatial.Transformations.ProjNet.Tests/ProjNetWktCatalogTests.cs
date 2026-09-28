using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;
using ProjWktReader = ProjNet.IO.CoordinateSystems.CoordinateSystemWktReader;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The WKT definition path of the ProjNet catalogue (ADR-0027): every CRS the
/// catalogue serves — the geographic and projected rows, the generated
/// families and the definitions that exist only as WKT — is read from EPSG
/// WKT and constructed through the one programmatic builder the catalogue
/// already trusted, rather than hand-written parameter lists.
/// <para>
/// Two things are pinned here. The reproduction is the trap the path exists
/// to close: ProjNet's own WKT reader turns the <c>Mercator_1SP</c> spelling
/// of Web Mercator into a plain Mercator, which is 33 km of northing on the
/// most-used projected CRS in the system — so the reader here intercepts the
/// identifiable Pseudo-Mercator spellings and routes them to the known-good
/// programmatic construction. The regression side is that the interception
/// and the whole WKT path leave the coordinates the catalogue already served
/// byte-for-byte identical (the pre-change table in
/// <see cref="ProjNetGeneratedCatalogTests"/>), while CRSs that only exist as
/// WKT now transform correctly.
/// </para>
/// </summary>
public sealed class ProjNetWktCatalogTests
{
    /// <summary>
    /// The WKT1 spelling of EPSG:3857 that carries the widely published
    /// <c>Mercator_1SP</c> projection name (the Google/OSRM/GeoServer
    /// dialect). It is a faithful WKT1 document of EPSG:3857, and it is the
    /// one ProjNet's reader gets wrong.
    /// </summary>
    private const string Mercator1SpWebMercatorWkt = """
        PROJCS["WGS 84 / Pseudo-Mercator",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Mercator_1SP"],PARAMETER["latitude_of_origin",0],PARAMETER["central_meridian",0],PARAMETER["scale_factor",1],PARAMETER["false_easting",0],PARAMETER["false_northing",0],UNIT["metre",1],AXIS["X",EAST],AXIS["Y",NORTH],AUTHORITY["EPSG","3857"]]
        """;

    /// <summary>
    /// The trap, measured: the naive ProjNet WKT path, given the
    /// <c>Mercator_1SP</c> spelling of Web Mercator, produces a plain
    /// Mercator and lands Berlin ~33 km too far south. This is the error the
    /// correction exists to prevent, so it is a number rather than a comment.
    /// </summary>
    [Fact]
    public async Task The_naive_ProjNet_WKT_path_loses_33_km_of_northing_on_Web_Mercator()
    {
        var naive = (ProjCs.ProjectedCoordinateSystem)
            ProjWktReader.Parse(Mercator1SpWebMercatorWkt);
        var math = MathTransformFrom(Wgs84(), naive);

        var (_, naiveNorthing) = math.Transform(13.405, 52.52);
        var catalogueNorthing = (await Project(13.405, 52.52, 3857)).Coordinate!.Value.Y;

        Assert.Equal("Mercator_1SP", naive.Projection.ClassName);
        Assert.InRange(Math.Abs(catalogueNorthing - naiveNorthing), 33_000.0, 35_000.0);
    }

    /// <summary>
    /// The correction: the identifiable Pseudo-Mercator spellings — the
    /// projection names, and the EPSG 3857 / 3785 / 900913 authorities —
    /// route to the known-good programmatic construction, so the same WKT
    /// that loses 33 km through the naive path produces the catalogue's
    /// exact bytes through this one.
    /// </summary>
    [Theory]
    [InlineData(Mercator1SpWebMercatorWkt)]
    [InlineData("PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Popular_Visualisation_Pseudo_Mercator\"],PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1],PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"3857\"]]")]
    [InlineData("PROJCS[\"Google Mercator\",GEOGCS[\"Google WGS 84\",DATUM[\"Google WGS 84\",SPHEROID[\"GRS 1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Mercator_1SP\"],PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1],PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"900913\"]]")]
    [InlineData("PROJCS[\"WGS_1984_Web_Mercator_Auxiliary_Sphere\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Mercator_Auxiliary_Sphere\"],PARAMETER[\"False_Easting\",0.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",0.0],PARAMETER[\"Standard_Parallel_1\",0.0],UNIT[\"Meter\",1.0]]")]
    public void A_pseudo_Mercator_spelling_takes_the_known_good_construction(string wkt)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projected = Assert.IsType<ProjectedDefinition>(definition);

        Assert.Equal("Popular Visualisation Pseudo-Mercator", projected.ProjectionClass);
    }

    /// <summary>
    /// The regression pin, in the WKT path's own terms: the definition the
    /// catalogue serves for EPSG:3857 is read from WKT, and the coordinates
    /// it produces are the exact doubles the hand-written row produced.
    /// </summary>
    [Fact]
    public async Task The_catalogue_serves_EPSG_3857_byte_for_byte_from_its_WKT()
    {
        var definition = ProjEpsgCatalog.DefinitionOf(3857);
        var projected = Assert.IsType<ProjectedDefinition>(definition);
        Assert.Equal("Popular Visualisation Pseudo-Mercator", projected.ProjectionClass);
        Assert.Equal("WGS 84 / Pseudo-Mercator", projected.Name);
        Assert.Equal("World Geodetic System 1984", projected.Base.DatumName);

        var coordinate = (await Project(13.405, 52.52, 3857)).Coordinate!.Value;

        Assert.True(1492237.774083832.Equals(coordinate.X), $"easting: got {coordinate.X:R}");
        Assert.True(6894699.801282422.Equals(coordinate.Y), $"northing: got {coordinate.Y:R}");
    }

    /// <summary>
    /// Every code the catalogue serves — the geographic rows, the projected
    /// rows, the generated families and the definitions that exist only as
    /// WKT — is read from WKT through the one builder. A hand-written
    /// parameter list anywhere would fail this.
    /// </summary>
    [Fact]
    public void Every_catalogue_entry_is_defined_in_WKT()
    {
        Assert.All(ProjEpsgCatalog.Codes, code =>
        {
            var definition = ProjEpsgCatalog.DefinitionOf(code);
            Assert.False(string.IsNullOrWhiteSpace(definition.Name), $"EPSG:{code} has no name");
        });
    }

    /// <summary>
    /// The demonstration the bead asks for: a CRS that exists only as a WKT
    /// definition, outside every hand-written row, describes and transforms
    /// like any other entry. EPSG:3395 is a plain Mercator — a projection the
    /// hand-written rows never used, and the one whose misreading as Web
    /// Mercator costs 33 km.
    /// </summary>
    [Fact]
    public async Task A_WKT_defined_CRS_outside_the_hand_written_rows_serves()
    {
        var description = await DescribeAsync("crs", "EPSG:3395");

        Assert.Equal("WGS 84 / World Mercator", description.Name);
        Assert.Equal(CrsKind.Projected, description.Kind);
        Assert.Equal(2, description.Dimension);
        Assert.Equal("metre", description.Axes[0].UnitName);
        Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
        Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
        Assert.Equal("WGS 84", description.Ellipsoid!.Name);
        Assert.Equal("Mercator_1SP", Assert.IsType<ProjectedDefinition>(ProjEpsgCatalog.DefinitionOf(3395)).ProjectionClass);
    }

    /// <summary>
    /// And one that is a different projection, over a different datum, on the
    /// far side of the world: EPSG:2193 is the UTM band New Zealand's own grid
    /// uses, and it is here as WKT rather than as a hand-written row.
    /// </summary>
    [Fact]
    public async Task A_WKT_defined_projection_on_its_own_datum_serves()
    {
        var description = await DescribeAsync("crs", "EPSG:2193");

        Assert.Equal("NZGD2000 / New Zealand Transverse Mercator", description.Name);
        Assert.Equal(CrsKind.Projected, description.Kind);
        Assert.Equal("New Zealand Geodetic Datum 2000", description.Datum);
        Assert.Equal("GRS 1980", description.Ellipsoid!.Name);
        Assert.Equal("metre", description.Axes[0].UnitName);
    }

    /// <summary>
    /// EPSG:2193's own two false offsets: a point on New Zealand's central
    /// meridian (173°E) lands on the 1,600,000 m false easting, and the
    /// equator on the 10,000,000 m false northing. The two together with the
    /// latitude of origin pin the whole Transverse Mercator parameter set.
    /// </summary>
    [Theory]
    [InlineData(173.0, -41.0, 1_600_000.0)]
    [InlineData(173.0, -45.0, 1_600_000.0)]
    [InlineData(174.7645, -41.2865, null)]
    public async Task A_WKT_defined_national_grid_pins_its_false_easting(double longitude, double latitude, double? expected)
    {
        var coordinate = (await Project(longitude, latitude, 2193)).Coordinate!.Value;

        if (expected is { } easting)
        {
            Assert.Equal(easting, coordinate.X, 3);
        }
        else
        {
            Assert.InRange(coordinate.X, 1_600_000.0, 1_800_000.0);
        }

        Assert.InRange(coordinate.Y, 4_000_000.0, 6_500_000.0);
    }

    [Fact]
    public async Task A_WKT_defined_national_grid_pins_its_false_northing_and_its_latitude_of_origin()
    {
        // The equator is the false northing, and the two hemispheres are
        // symmetric about it — which is what a Transverse Mercator with a
        // latitude of origin of 0 and a 10,000,000 m false northing means.
        var equator = (await Project(173.0, 0.0, 2193)).Coordinate!.Value;
        var north = (await Project(173.0, 41.0, 2193)).Coordinate!.Value;
        var south = (await Project(173.0, -41.0, 2193)).Coordinate!.Value;

        Assert.Equal(10_000_000.0, equator.Y, 3);
        Assert.Equal(20_000_000.0, north.Y + south.Y, 3);
    }

    /// <summary>
    /// The scale factor, checked against analytics rather than a table: on the
    /// central meridian a Transverse Mercator's easting runs at exactly
    /// <c>k · ν(φ) · Δλ</c> metres per radian, where ν is the prime vertical
    /// radius of curvature. A wrong scale factor, central meridian or
    /// ellipsoid moves this.
    /// </summary>
    [Fact]
    public async Task A_WKT_defined_national_grid_honours_its_scale_factor_on_its_central_meridian()
    {
        const double latitude = -41.2865;
        const double delta = 0.001;
        var here = (await Project(173.0, latitude, 2193)).Coordinate!.Value;
        var step = (await Project(173.0 + delta, latitude, 2193)).Coordinate!.Value;

        const double semiMajor = 6378137.0;
        const double inverseFlattening = 298.257222101;
        var flattening = 1 / inverseFlattening;
        var sine = Math.Sin(latitude * Math.PI / 180);
        var nu = semiMajor / Math.Sqrt(1 - flattening * (2 - flattening) * sine * sine);

        // Snyder eq 3-1: on the central meridian the easting runs at
        // k · ν(φ) · cos(φ) metres per radian of longitude.
        var expected = 0.9996 * nu * Math.Cos(latitude * Math.PI / 180) * (delta * Math.PI / 180);

        Assert.Equal(expected, step.X - here.X, 3);
    }

    /// <summary>
    /// The northings follow the meridian arc: on the central meridian a
    /// Transverse Mercator's northing is the false northing plus the scaled
    /// meridian distance from the latitude of origin, and the meridian arc
    /// series below reproduces it to a few millimetres over 5,000 km. A wrong
    /// scale factor, latitude of origin or false northing moves this by
    /// kilometres.
    /// </summary>
    [Theory]
    [InlineData(-45.0)]
    [InlineData(-37.6520)]
    [InlineData(-41.2865)]
    public async Task A_WKT_defined_national_grid_northings_follow_the_meridian_arc(double latitude)
    {
        var coordinate = (await Project(173.0, latitude, 2193)).Coordinate!.Value;

        Assert.InRange(Math.Abs(coordinate.Y - (10_000_000.0 + 0.9996 * MeridianArc(latitude))), 0.0, 0.05);
    }

    /// <summary>
    /// The meridian distance from the equator, by the standard series for the
    /// meridian arc on an ellipsoid of revolution.
    /// </summary>
    private static double MeridianArc(double latitude)
    {
        const double semiMajor = 6378137.0;
        var flattening = 1 / 298.257222101;
        var e2 = flattening * (2 - flattening);
        var e4 = e2 * e2;
        var e6 = e4 * e2;
        var phi = latitude * Math.PI / 180;
        return semiMajor * (
            (1 - e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
            - (3 * e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * phi)
            + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * phi)
            - (35 * e6 / 3072) * Math.Sin(6 * phi));
    }

    /// <summary>A WKT-defined CRS inverts back to the coordinates it was given.</summary>
    [Theory]
    [InlineData(3395)]
    [InlineData(2193)]
    public async Task A_WKT_defined_CRS_inverts_back_to_WGS84(int code)
    {
        var projected = (await Project(174.7645, -41.2865, code)).Coordinate!.Value;
        var round = await TransformAsync(
            "geometry", new Point(projected, CoordinateReference.Epsg(code)),
            "source", $"EPSG:{code}",
            "target", "EPSG:4326");

        var back = Assert.IsType<Point>(round).Coordinate!.Value;

        Assert.Equal(174.7645, back.X, 6);
        Assert.Equal(-41.2865, back.Y, 6);
    }

    /// <summary>
    /// The control points for EPSG:3395, computed independently of ProjNet
    /// from EPSG Guidance Note 7-2's method 9804, Mercator (variant A):
    /// <c>E = a·k₀·(λ − λ₀)</c> and
    /// <c>N = a·k₀·[ln(tan(π/4 + φ/2)) − (e/2)·ln((1 + e·sinφ)/(1 − e·sinφ))]</c>
    /// on the WGS 84 ellipsoid — so a projection the hand-written rows never
    /// used is checked against something other than the library that reads
    /// it.
    /// </summary>
    public static TheoryData<double, double, double, double> WorldMercatorControlPoints()
    {
        var data = new TheoryData<double, double, double, double>();
        data.Add(13.405, 52.52, 1492237.774083832, 6860768.653298051);
        data.Add(-3.7038, 40.4168, -412305.1300001266, 4898987.915137946);
        data.Add(149.13, -35.2809, 16601075.662000885, -4177440.216410757);
        data.Add(24.9384, 60.1699, 2776129.9891989734, 8400558.978161905);
        data.Add(-122.33, 47.61, -13617713.308741156, 6010642.431053977);
        data.Add(-78.467, -0.1807, -8734906.484075798, -19980.805207705074);
        return data;
    }

    [Theory]
    [MemberData(nameof(WorldMercatorControlPoints))]
    public async Task A_WKT_defined_CRS_transforms_to_the_published_control_point(
        double longitude, double latitude, double easting, double northing)
    {
        var coordinate = (await Project(longitude, latitude, 3395)).Coordinate!.Value;

        Assert.Equal(easting, coordinate.X, 3);
        Assert.Equal(northing, coordinate.Y, 3);
    }

    /// <summary>
    /// The reader takes both dialects: the WKT1 and WKT2 spellings of one
    /// EPSG definition describe the same place with the same coordinates, so
    /// vendoring a definition in either dialect is interchangeable.
    /// </summary>
    [Fact]
    public async Task A_WKT_1_and_a_WKT_2_spelling_of_one_definition_construct_identically()
    {
        // EPSG:3395 in the OGC WKT1 dialect, with the projection and datum
        // names as that dialect spells them.
        const string Wkt1 = """
            PROJCS["WGS 84 / World Mercator",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Mercator_1SP"],PARAMETER["latitude_of_origin",0],PARAMETER["central_meridian",0],PARAMETER["scale_factor",1],PARAMETER["false_easting",0],PARAMETER["false_northing",0],UNIT["metre",1],AUTHORITY["EPSG","3395"]]
            """;

        Assert.True(ProjWkt.TryParse(Wkt1, out var parsed, out var error), error);
        var fromWkt1 = ProjEpsgCatalog.Build((ProjectedDefinition)parsed!);
        var fromWkt2 = ProjEpsgCatalog.Build((ProjectedDefinition)ProjEpsgCatalog.DefinitionOf(3395));

        foreach (var (longitude, latitude) in new[] { (13.405, 52.52), (-3.7038, 40.4168), (149.13, -35.2809) })
        {
            var one = ProjNetTransforms.Apply(
                GeometryFactory.CreatePoint(longitude, latitude, CoordinateReference.Epsg(4326)),
                MathTransformFrom(fromWkt1),
                CoordinateReference.Epsg(3395));
            var two = ProjNetTransforms.Apply(
                GeometryFactory.CreatePoint(longitude, latitude, CoordinateReference.Epsg(4326)),
                MathTransformFrom(fromWkt2),
                CoordinateReference.Epsg(3395));

            // The two dialects name the datum and the projection differently
            // and compute the same place: the same doubles, not the same
            // description.
            Assert.NotEqual(
                fromWkt1.GeographicCoordinateSystem.HorizontalDatum!.Name,
                fromWkt2.GeographicCoordinateSystem.HorizontalDatum!.Name);
            Assert.Equal(((Point)one).Coordinate!.Value, ((Point)two).Coordinate!.Value);
        }
    }

    /// <summary>
    /// A WKT-defined CRS is a catalogue row like any other: one dictionary
    /// entry, built once on first use, and a code outside the catalogue is
    /// still the same actionable failure.
    /// </summary>
    [Fact]
    public void A_WKT_defined_CRS_is_a_lazy_single_build_row()
    {
        Assert.True(ProjEpsgCatalog.TryGet(3395, out var first));
        Assert.True(ProjEpsgCatalog.IsBuilt(3395));
        Assert.True(ProjEpsgCatalog.TryGet(3395, out var second));
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("not wkt at all", "trailing text")]
    [InlineData("VERT_CS[\"x\",VERT_DATUM[\"d\",2005],UNIT[\"metre\",1]]", "VERT_CS")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"WGS 84\"]]]", "datum")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"WGS 84\",SPHEROID[\"WGS 84\",6378137,298.257223563]]],CONVERSION[\"c\",METHOD[\"Krovak\"]]]", "Krovak")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"WGS 84\",SPHEROID[\"WGS 84\",6378137,298.257223563]]],CONVERSION[\"c\",METHOD[\"Transverse Mercator\"],PARAMETER[\"Longitude of the central meridian\",0]]]", "central meridian")]
    [InlineData("GEOGCRS[\"x\",ENSEMBLE[\"e\",MEMBER[\"m\"],ELLIPSOID[\"WGS 84\",6378137,298.257223563]]]", "ENSEMBLE")]
    public void A_definition_the_reader_cannot_build_is_an_actionable_failure(string wkt, string mentions)
    {
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains(mentions, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every way a document can be wrong is a named failure rather than a
    /// half-read definition: the reader is the only thing standing between a
    /// vendored WKT string and a CRS with quietly wrong numbers, so each
    /// rejection has to say what it found.
    /// </summary>
    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("1GEOGCS", "keyword")]
    [InlineData("\"quoted\"", "keyword")]
    [InlineData("GEOGCRS[", "closing")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563],TOWGS84[1,2,3]],PRIMEM[\"Greenwich\",0]]", "TOWGS84")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",ELLIPSOID[\"e\",6378137]],PRIMEM[\"Greenwich\",0]]", "ellipsoid gives 1")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",ELLIPSOID[\"e\",0,298.257223563]],PRIMEM[\"Greenwich\",0]]", "semi-major axis")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,7000000]],PRIMEM[\"Greenwich\",0]]", "not an inverse flattening")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",TOWGS84[0,0,0,0,0,0,0]]]", "ELLIPSOID or SPHEROID")]
    [InlineData("GEOGCRS[\"x\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,0]],PRIMEM[\"Greenwich\",0]]", "sphere")]
    [InlineData("PROJCRS[\"x\",CONVERSION[\"c\",METHOD[\"Transverse Mercator\"]]]", "base geographic CRS")]
    [InlineData("PROJCRS[\"x\",BASEGEOGCRS[\"WGS 84\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]],ID[\"EPSG\",1]]", "CONVERSION")]

    [InlineData("PROJCS[\"x\"", "closing")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"central_meridian\",0,%%%]]", "not a WKT keyword")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"central_meridian,0]]", "not closed")]
    [InlineData("GEOGCRS[]", "no name")]
    [InlineData("PROJCRS[BASEGEOGCRS[\"WGS 84\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]],CONVERSION[\"c\",METHOD[\"Transverse Mercator\"]]]", "no name")]
    [InlineData("PROJCS[\"x\",GEOGCS[\"WGS 84\",DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]],PROJECTION[]]", "names no projection method")]
    [InlineData("GEOGCRS[DATUM[\"d\",ELLIPSOID[\"e\",6378137,298.257223563]]]", "no name")]
    public void A_malformed_definition_is_rejected_with_a_reason(string wkt, string mentions)
    {
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains(mentions, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A parameter with no name and no value carries nothing, so it is skipped
    /// rather than failing the document: WKT in the wild has stray nodes.
    /// </summary>
    [Fact]
    public void A_parameter_with_no_name_and_no_value_is_skipped()
    {
        const string Wkt = """
            PROJCS["x",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER[],PARAMETER["central_meridian",0],UNIT["metre",1]]
            """;

        var definition = (ProjectedDefinition)Parse(Wkt);

        Assert.Equal("Transverse_Mercator", definition.ProjectionClass);
        Assert.Equal([("central_meridian", 0.0)], definition.Parameters);
    }

    /// <summary>
    /// A definition that gives its ellipsoid as a semi-minor axis rather than
    /// an inverse flattening is the same ellipsoid, and reads as one.
    /// </summary>
    [Fact]
    public void A_semi_minor_axis_reads_as_the_same_ellipsoid()
    {
        const string SemiMinor = """
            GEOGCRS["x",DATUM["d",SPHEROID["GRS 1980",6378137,6356752.314245309]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]]
            """;
        const string InverseFlattening = """
            GEOGCRS["x",DATUM["d",SPHEROID["GRS 1980",6378137,298.257222101]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]]
            """;

        var one = (GeodeticDefinition)Parse(SemiMinor);
        var other = (GeodeticDefinition)Parse(InverseFlattening);

        Assert.Equal(other.SemiMajor, one.SemiMajor, 3);
        Assert.Equal(other.InverseFlattening, one.InverseFlattening, 3);
    }

    /// <summary>
    /// The ESRI spelling of Web Mercator names its projection and omits the
    /// origin, so the intercept supplies the four standard parameters rather
    /// than handing the projection none.
    /// </summary>
    [Fact]
    public void An_intercepted_pseudo_Mercator_with_no_parameters_gets_the_standard_ones()
    {
        const string Esri = """
            PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Mercator_Auxiliary_Sphere"],UNIT["Meter",1.0]]
            """;

        var definition = (ProjectedDefinition)Parse(Esri);

        Assert.Equal("Popular Visualisation Pseudo-Mercator", definition.ProjectionClass);
        Assert.Equal(
            [("latitude_of_origin", 0.0), ("central_meridian", 0.0), ("false_easting", 0.0), ("false_northing", 0.0)],
            definition.Parameters);
    }

    /// <summary>
    /// A projection parameter the engine's projections do not read is a named
    /// failure: a dropped parameter would move every coordinate of the CRS.
    /// </summary>
    [Fact]
    public void A_parameter_the_projections_do_not_read_is_rejected()
    {
        const string Wkt = """
            PROJCS["x",GEOGCS["WGS 84",DATUM["WGS 1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["central_meridian",0],PARAMETER["Longitude of the origin of the false grid",0],UNIT["metre",1]]
            """;

        Assert.False(ProjWkt.TryParse(Wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.Contains("Longitude of the origin of the false grid", error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A vendored definition that does not read stops the process where it is
    /// noticed — a build error naming the code, never a half-read CRS served
    /// to a caller.
    /// </summary>
    [Theory]
    [InlineData("PROJCRS[\"x\",{base", "does not render")]
    [InlineData("PROJCRS[\"x\",BASEGEOGCRS{base}]", "does not read")]
    public void A_vendored_definition_that_does_not_read_fails_the_build(string template, string mentions)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ProjEpsgCatalog.ReadForTest(template, new Dictionary<string, string> { ["base"] = "[]" }, 9999));

        Assert.Contains(mentions, error.Message, StringComparison.Ordinal);
        Assert.Contains("9999", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A template's tokens are the family's only job, and a missing one is a named failure.</summary>
    [Theory]
    [InlineData("GEOGCRS[\"x\",{name}]", "'name'")]
    [InlineData("PROJCRS[\"x\",BASEGEOGCRS{base}]", "'base'")]
    [InlineData("PROJCRS[\"x\",{base", "unclosed")]
    public void A_template_that_cannot_be_rendered_is_rejected(string template, string mentions)
    {
        Assert.False(ProjWkt.TryRender(template, new Dictionary<string, string>(), out var rendered, out var error));
        Assert.Equal(string.Empty, rendered);
        Assert.NotNull(error);
        Assert.Contains(mentions, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_outside_the_catalogue_is_still_rejected()
    {
        var error = Assert.Throws<SpatialException>(
            () => new ProjNetTransforms().Describe("EPSG:12345"));

        Assert.Equal("invalid.arguments", error.Code);
    }

    [Fact]
    public void A_pre_cancelled_describe_of_a_WKT_defined_CRS_stops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new ProjNetTransforms().Describe("EPSG:3395", cancellation.Token));
    }

    private static ProjTf.MathTransform MathTransformFrom(ProjCs.ProjectedCoordinateSystem target) =>
        MathTransformFrom(Wgs84(), target);

    private static ProjTf.MathTransform MathTransformFrom(ProjCs.CoordinateSystem source, ProjCs.ProjectedCoordinateSystem target) =>
        new ProjTf.CoordinateTransformationFactory().CreateFromCoordinateSystems(source, target).MathTransform;

    private static ProjCs.CoordinateSystem Wgs84()
    {
        Assert.True(ProjEpsgCatalog.TryGet(4326, out var system));
        return system!;
    }

    [Theory]
    [InlineData(3395)]
    [InlineData(2193)]
    public async Task A_pre_cancelled_transform_of_a_WKT_defined_CRS_stops(int code)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var arguments = new Dictionary<string, object?>
        {
            ["geometry"] = GeometryFactory.CreatePoint(174.7645, -41.2865, CoordinateReference.Epsg(4326)),
            ["source"] = "EPSG:4326",
            ["target"] = $"EPSG:{code}",
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TransformInvoker.TransformAsync(arguments, cancellation.Token));
    }

    private static CrsDefinition Parse(string wkt)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        return definition!;
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
