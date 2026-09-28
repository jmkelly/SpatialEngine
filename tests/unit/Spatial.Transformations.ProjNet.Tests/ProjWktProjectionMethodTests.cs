using Spatial.Contracts;
using Spatial.Core.Geometry;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The WKT method map measured against PROJ (SpatialEngine-u2x.26).
/// <para>
/// <see cref="ProjWkt"/> resolves a WKT projection method to a ProjNet
/// projection, and that is a claim that the two compute the same thing — so
/// each method in the map is measured here against PROJ 9.8.1 (through
/// pyproj, always_xy, on the definition's own ellipsoid and with no datum
/// shift, so what is compared is the projection and nothing else), forward
/// and inverse, at points inside each definition's area of use. The control
/// points below are PROJ's own output for the named EPSG code, not the
/// library's.
/// </para>
/// <para>
/// The map holds the methods that agree, and the ones that do not are named
/// failures rather than served coordinates: a method whose parameters or whose
/// axis convention ProjNet cannot express is a gap the reader reports by name
/// (and the gap is characterised in a test, with the measured size of the
/// error, so a future change that closes it has to move the number).
/// </para>
/// </summary>
public sealed class ProjWktProjectionMethodTests
{
    /// <summary>
    /// EPSG:5070 NAD83 / Conus Albers, Albers Equal Area (EPSG method 9822).
    /// The Albers the earlier measurement could not account for: measured
    /// here because the method map refuses to widen on a projection nobody has
    /// checked.
    /// </summary>
    private const string ConusAlbers = """
        PROJCRS["NAD83 / Conus Albers",BASEGEOGCRS["NAD83",DATUM["North American Datum 1983",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4269]],CONVERSION["Conus Albers",METHOD["Albers Equal Area",ID["EPSG",9822]],PARAMETER["Latitude of false origin",23,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8821]],PARAMETER["Longitude of false origin",-96,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8822]],PARAMETER["Latitude of 1st standard parallel",29.5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8823]],PARAMETER["Latitude of 2nd standard parallel",45.5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8824]],PARAMETER["Easting at false origin",0,LENGTHUNIT["metre",1],ID["EPSG",8826]],PARAMETER["Northing at false origin",0,LENGTHUNIT["metre",1],ID["EPSG",8827]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",5070]]
        """;

    /// <summary>
    /// EPSG:3035 ETRS89-extended / LAEA Europe, Lambert Azimuthal Equal Area
    /// (EPSG method 9820) — the four false offsets of 4,321 km and 3,210 km
    /// mean a projection that ignores them is wrong by 4,321 km, so this is
    /// the measurement that can catch it.
    /// </summary>
    private const string LaeaEurope = """
        PROJCRS["ETRS89-extended / LAEA Europe",BASEGEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4258]],CONVERSION["Europe Equal Area 2001",METHOD["Lambert Azimuthal Equal Area",ID["EPSG",9820]],PARAMETER["Latitude of natural origin",52,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",10,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["False easting",4321000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["Northing at false origin",3210000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3035]]
        """;

    /// <summary>
    /// EPSG:5041 WGS 84 / UPS North, Polar Stereographic (variant A, EPSG
    /// method 9810) — the universal polar case, 2,000 km of false offsets in
    /// each axis.
    /// </summary>
    private const string UpsNorth = """
        PROJCRS["WGS 84 / UPS North (E,N)",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["Universal Polar Stereographic North",METHOD["Polar Stereographic (variant A)",ID["EPSG",9810]],PARAMETER["Latitude of natural origin",90,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["Scale factor at natural origin",0.994,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["False easting",2000000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",2000000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",5041]]
        """;

    /// <summary>
    /// EPSG:2056 CH1903+ / LV95, Hotine Oblique Mercator (variant B, EPSG
    /// method 9815) — the Swiss grid, whose false offsets sit at the
    /// projection centre.
    /// </summary>
    private const string SwissObliqueMercator = """
        PROJCRS["CH1903+ / LV95",BASEGEOGCRS["CH1903+",DATUM["CH1903+",ELLIPSOID["Bessel 1841",6377397.155,299.1528128,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4150]],CONVERSION["Swiss Oblique Mercator 1995",METHOD["Hotine Oblique Mercator (variant B)",ID["EPSG",9815]],PARAMETER["Latitude of projection centre",46.9524055555556,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",7.43958333333333,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth of initial line",90,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",90,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",1,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["Easting at projection centre",2600000,LENGTHUNIT["metre",1],ID["EPSG",8816]],PARAMETER["Northing at projection centre",1200000,LENGTHUNIT["metre",1],ID["EPSG",8817]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",2056]]
        """;

    /// <summary>
    /// EPSG:3078 NAD83 / Michigan Oblique Mercator, Hotine Oblique Mercator
    /// (variant A, EPSG method 9812) — the one variant-A definition whose
    /// false offsets PROJ applies at the natural origin rather than at the
    /// projection centre.
    /// </summary>
    private const string MichiganObliqueMercator = """
        PROJCRS["NAD83 / Michigan Oblique Mercator",BASEGEOGCRS["NAD83",DATUM["North American Datum 1983",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4269]],CONVERSION["Michigan Oblique Mercator (meter)",METHOD["Hotine Oblique Mercator (variant A)",ID["EPSG",9812]],PARAMETER["Latitude of projection centre",45.3091666666667,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",-86,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth of initial line",337.25556,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",337.25556,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",0.9996,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["Easting at projection centre",2546731.4967949,LENGTHUNIT["metre",1],ID["EPSG",8816]],PARAMETER["Northing at projection centre",-4354009.8168466,LENGTHUNIT["metre",1],ID["EPSG",8817]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3078]]
        """;

    /// <summary>
    /// EPSG:3031 WGS 84 / Antarctic Polar Stereographic, Polar Stereographic
    /// (variant B, EPSG method 9829) — stated by its latitude of standard
    /// parallel rather than by a scale factor at a pole.
    /// </summary>
    private const string AntarcticPolarStereographic = """
        PROJCRS["WGS 84 / Antarctic Polar Stereographic",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["Antarctic Polar Stereographic",METHOD["Polar Stereographic (variant B)",ID["EPSG",9829]],PARAMETER["Latitude of standard parallel",-71,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8832]],PARAMETER["Longitude of origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8836]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8837]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3031]]
        """;

    /// <summary>
    /// EPSG:5513 S-JTSK / Krovak, Krovak (EPSG method 9819) — the axes are
    /// the ones the method's own convention produces, which are not
    /// easting-and-northing.
    /// </summary>
    private const string Krovak = """
        PROJCRS["S-JTSK / Krovak",BASEGEOGCRS["S-JTSK",DATUM["System of the Unified Trigonometrical Cadastral Network",ELLIPSOID["Bessel 1841",6377397.155,299.1528128,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",5513]],CONVERSION["Krovak",METHOD["Krovak",ID["EPSG",9819]],PARAMETER["Latitude of projection centre",49.5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of origin",24.8333333333333,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["Co-latitude of cone axis",30.2881397527778,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",1036]],PARAMETER["Latitude of pseudo standard parallel",78.5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8832]],PARAMETER["Scale factor on pseudo standard parallel",0.9999,SCALEUNIT["unity",1],ID["EPSG",8818]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["Northing at false origin",0,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(X)",south,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(Y)",west,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",5513]]
        """;

    /// <summary>
    /// The methods the reader resolves, and the ProjNet projection each one
    /// resolves to. A method that is not in this list is a named failure.
    /// </summary>
    [Theory]
    [InlineData(ConusAlbers, "Albers Equal Area", "Albers_Conic_Equal_Area")]
    [InlineData(LaeaEurope, "Lambert Azimuthal Equal Area", "Lambert_Azimuthal_Equal_Area")]
    [InlineData(UpsNorth, "Polar Stereographic (variant A)", "Polar_Stereographic")]
    [InlineData(SwissObliqueMercator, "Hotine Oblique Mercator (variant B)", "Hotine_Oblique_Mercator")]
    public void A_method_verified_against_PROJ_resolves_to_its_ProjNet_projection(
        string wkt, string method, string projectionClass)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projected = Assert.IsType<ProjectedDefinition>(definition);

        Assert.Equal(projectionClass, projected.ProjectionClass);
        Assert.Contains(method, wkt, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROJ 9.8.1's own coordinates for each verified definition: Seattle,
    /// Chicago, Los Angeles and the projection origin on the Conus Albers
    /// parameters; four points across Europe on the LAEA Europe parameters;
    /// four points in the northern polar cap on the UPS North parameters; and
    /// three points in Switzerland on the LV95 parameters.
    /// </summary>
    public static TheoryData<string, double, double, double, double> ProjControlPoints()
    {
        var data = new TheoryData<string, double, double, double, double>();

        // EPSG:5070, NAD83 / Conus Albers.
        data.Add(ConusAlbers, -122.33, 47.6, -1968025.5402744517, 3008409.0761157167);
        data.Add(ConusAlbers, -87.63, 41.88, 688859.4285168712, 2127843.0480303406);
        data.Add(ConusAlbers, -120.0, 34.0, -2177334.7661539624, 1491382.7731987587);
        data.Add(ConusAlbers, -96.0, 23.0, 0.0, 0.0);

        // EPSG:3035, ETRS89-extended / LAEA Europe.
        data.Add(LaeaEurope, 10.0, 50.0, 4321000.0, 2987510.566964945);
        data.Add(LaeaEurope, 0.0, 45.0, 3533853.425868904, 2484861.2023799517);
        data.Add(LaeaEurope, -10.0, 40.0, 2626483.081788042, 2103594.616626961);
        data.Add(LaeaEurope, 25.0, 60.0, 5152827.253890325, 4188383.6111051245);

        // EPSG:5041, WGS 84 / UPS North (E,N).
        data.Add(UpsNorth, -100.0, 80.0, 903957.0916030575, 2193261.9367645574);
        data.Add(UpsNorth, 0.0, 89.0, 2000000.0, 1888973.4798806852);
        data.Add(UpsNorth, 45.0, 70.0, 3585609.011733939, 414390.9882660608);
        data.Add(UpsNorth, 179.0, 85.0, 2009694.0681530037, 2555372.792531552);

        // EPSG:2056, CH1903+ / LV95.
        data.Add(SwissObliqueMercator, 8.0, 47.0, 2642617.528074719, 1205442.8138998896);
        data.Add(SwissObliqueMercator, 6.0, 46.0, 2488489.649525768, 1095160.8587208204);
        data.Add(SwissObliqueMercator, 10.0, 46.5, 2796491.3125768597, 1152921.7672456983);

        return data;
    }

    /// <summary>
    /// The measurement itself: a method is in the map because ProjNet's
    /// projection lands on PROJ's coordinates for that EPSG definition —
    /// forward.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProjControlPoints))]
    public void A_method_verified_against_PROJ_transforms_to_the_PROJ_coordinate(
        string wkt, double longitude, double latitude, double easting, double northing)
    {
        var (x, y) = Project(wkt, longitude, latitude);

        Assert.Equal(easting, x, 3);
        Assert.Equal(northing, y, 3);
    }

    /// <summary>
    /// And inverse: the same round trip PROJ does, so a method that is right
    /// forwards and wrong backwards is not in the map either.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProjControlPoints))]
    public void A_method_verified_against_PROJ_inverts_back_to_the_given_point(
        string wkt, double longitude, double latitude, double easting, double northing)
    {
        // Inverted from PROJ's own coordinates, not from the engine's, so a
        // wrong inverse cannot hide behind a wrong forward.
        var (backLongitude, backLatitude) = Inverse(wkt, easting, northing);

        Assert.Equal(longitude, backLongitude, 6);
        Assert.Equal(latitude, backLatitude, 6);
    }

    /// <summary>
    /// The methods measured and found to diverge are named failures, not
    /// served coordinates: the reader reports the method it will not build,
    /// so a definition in one of them is an actionable error rather than a
    /// CRS with quietly wrong numbers.
    /// </summary>
    [Theory]
    [InlineData(MichiganObliqueMercator, "Hotine Oblique Mercator (variant A)")]
    [InlineData(AntarcticPolarStereographic, "Polar Stereographic (variant B)")]
    [InlineData(Krovak, "Krovak")]
    public void A_method_that_diverges_from_PROJ_is_refused_by_name(string wkt, string method)
    {
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains(method, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Hotine variant A divergence, sized. ProjNet's Hotine Oblique
    /// Mercator puts the projection centre on the false easting and northing —
    /// Snyder's <c>Alternate B</c> convention, where the false offsets are
    /// read as the coordinates of the centre — while PROJ's EPSG:3078 applies
    /// them at the natural origin, so the centre lands 2,046,891 m west and
    /// 4,882,610 m south of where EPSG publishes it and every other point
    /// with it. Measured at the centre: ProjNet gives the false origin
    /// exactly, PROJ gives (499840.25238587055, 528600.3033698238).
    /// </summary>
    [Fact]
    public void The_Hotine_variant_A_divergence_is_two_thousand_kilometres_of_false_offset()
    {
        var (centreEasting, centreNorthing) = Direct(
            "Hotine_Oblique_Mercator",
            [
                Parameter("latitude_of_center", 45.30916666666666),
                Parameter("longitude_of_center", -86.0),
                Parameter("azimuth", 337.25556),
                Parameter("rectified_grid_angle", 337.25556),
                Parameter("scale_factor", 0.9996),
                Parameter("false_easting", 2546731.4967949),
                Parameter("false_northing", -4354009.8168466),
            ],
            6378137.0,
            298.257222101,
            -86.0,
            45.30916666666666);

        // ProjNet's convention puts the centre on the false easting and
        // northing exactly...
        Assert.Equal(2546731.4967949, centreEasting, 6);
        Assert.Equal(-4354009.8168466, centreNorthing, 6);

        // ...where PROJ's puts it 2,046,891 m west and 4,882,610 m north of
        // that. Every point of the definition inherits the same error.
        Assert.InRange(centreEasting - 499840.25238587055, 2_046_000.0, 2_048_000.0);
        Assert.InRange(centreNorthing - 528600.3033698238, -4_884_000.0, -4_882_000.0);
    }

    /// <summary>
    /// The polar stereographic variant B gap, sized. ProjNet's polar
    /// stereographic is the variant A formulation — a pole, a scale factor and
    /// the false offsets — and it agrees with PROJ there to well under a
    /// micrometre. What it has no parameter for is EPSG's variant B
    /// parameterisation, the latitude of standard parallel, which PROJ turns
    /// into the scale factor 0.9727690128917965 for EPSG:3031. Handed that
    /// scale factor, ProjNet reproduces EPSG:3031 exactly; handed the
    /// definition as EPSG states it, it is off by 527 km of northing, so the
    /// method stays out of the map.
    /// </summary>
    [Fact]
    public void The_polar_stereographic_variant_B_gap_is_the_standard_parallel_parameter()
    {
        var (easting, northing) = Direct(
            "Polar_Stereographic",
            [
                Parameter("latitude_of_origin", -90.0),
                Parameter("central_meridian", 0.0),
                Parameter("scale_factor", 0.9727690128917965),
                Parameter("false_easting", 0.0),
                Parameter("false_northing", 0.0),
            ],
            6378137.0,
            298.257223563,
            0.0,
            -80.0);

        // PROJ 9.8.1 for EPSG:3031 at 0°E, 80°S.
        Assert.Equal(0.0, easting, 6);
        Assert.Equal(1089179.4556261837, northing, 6);

        // Left to its own parameters, the projection lands 527 km away.
        Assert.InRange(1089179.4556261837 - 561713.6916, 527_000.0, 528_000.0);
    }

    /// <summary>
    /// The Krovak divergence is not arithmetic but axes. The magnitudes
    /// agree with PROJ to the millimetre; the ordinates are the other way
    /// round and negative, because ProjNet's Krovak returns the
    /// south-oriented, west-positive convention the method was defined with,
    /// while EPSG:5513's own axes are southing and westing. The catalogue
    /// serves easting-and-northing axes, so a Krovak definition cannot be
    /// served without deciding which convention it is in.
    /// </summary>
    [Fact]
    public void The_Krovak_divergence_is_the_axis_convention_not_the_arithmetic()
    {
        var (x, y) = Direct(
            "Krovak",
            [
                Parameter("latitude_of_origin", 49.5),
                Parameter("central_meridian", 24.833333333333332),
                Parameter("azimuth", 30.28813975277778),
                Parameter("pseudo_standard_parallel_1", 78.5),
                Parameter("scale_factor", 0.9999),
                Parameter("false_easting", 0.0),
                Parameter("false_northing", 0.0),
            ],
            6377397.155,
            299.1528128,
            14.42,
            50.09);

        // PROJ 9.8.1 for EPSG:5513 at 14.42°E, 50.09°N is (1042796.9662,
        // 742949.4310) in that CRS's own southing/westing axis order;
        // ProjNet returns the same magnitudes, negated and transposed.
        Assert.Equal(-742949.431, x, 3);
        Assert.Equal(-1042796.9662, y, 3);
    }

    private static ProjCs.ProjectionParameter Parameter(string name, double value) => new(name, value);

    /// <summary>Projects one point through a ProjNet projection built directly, on the given ellipsoid.</summary>
    private static (double Easting, double Northing) Direct(
        string projectionClass,
        IReadOnlyCollection<ProjCs.ProjectionParameter> parameters,
        double semiMajor,
        double inverseFlattening,
        double longitude,
        double latitude)
    {
        var factory = new ProjCs.CoordinateSystemFactory();
        var geographic = Geographic(factory, semiMajor, inverseFlattening);
        var projected = factory.CreateProjectedCoordinateSystem(            "probe",
            geographic,
            factory.CreateProjection(projectionClass, projectionClass, [.. parameters]),
            ProjCs.LinearUnit.Metre,
            new ProjCs.AxisInfo("Easting", ProjCs.AxisOrientationEnum.East),
            new ProjCs.AxisInfo("Northing", ProjCs.AxisOrientationEnum.North));

        var transform = new ProjTf.CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(geographic, projected).MathTransform;

        return transform.Transform(longitude, latitude);
    }

    private static ProjCs.GeographicCoordinateSystem Geographic(
        ProjCs.CoordinateSystemFactory factory, double semiMajor, double inverseFlattening)
    {
        var ellipsoid = factory.CreateFlattenedSphere("probe", semiMajor, inverseFlattening, ProjCs.LinearUnit.Metre);
        var datum = factory.CreateHorizontalDatum(
            "probe", ProjCs.DatumType.HD_Geocentric, ellipsoid, new ProjCs.Wgs84ConversionInfo(0, 0, 0, 0, 0, 0, 0));

        return factory.CreateGeographicCoordinateSystem(
            "probe",
            ProjCs.AngularUnit.Degrees,
            datum,
            factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0),
            new ProjCs.AxisInfo("Lon", ProjCs.AxisOrientationEnum.East),
            new ProjCs.AxisInfo("Lat", ProjCs.AxisOrientationEnum.North));
    }

    /// <summary>Reads a WKT definition and projects one point through it, the way the catalogue builds it.</summary>
    private static (double Easting, double Northing) Project(string wkt, double longitude, double latitude)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projectedDefinition = Assert.IsType<ProjectedDefinition>(definition);
        var projected = ProjEpsgCatalog.Build(projectedDefinition);
        var baseSystem = Base(projectedDefinition.Base);

        var transform = new ProjTf.CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(baseSystem, projected).MathTransform;

        return transform.Transform(longitude, latitude);
    }

    private static (double Longitude, double Latitude) Inverse(string wkt, double easting, double northing)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projectedDefinition = Assert.IsType<ProjectedDefinition>(definition);
        var projected = ProjEpsgCatalog.Build(projectedDefinition);
        var baseSystem = Base(projectedDefinition.Base);

        var transform = new ProjTf.CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(projected, baseSystem).MathTransform;

        return transform.Transform(easting, northing);
    }

    /// <summary>
    /// The geographic CRS a definition projects from, built from the
    /// definition's own ellipsoid: the comparison here is the projection
    /// against PROJ, so the base is the same ellipsoid and nothing else.
    /// </summary>
    private static ProjCs.GeographicCoordinateSystem Base(GeodeticDefinition definition) =>
        Geographic(new ProjCs.CoordinateSystemFactory(), definition.SemiMajor, definition.InverseFlattening);
}
