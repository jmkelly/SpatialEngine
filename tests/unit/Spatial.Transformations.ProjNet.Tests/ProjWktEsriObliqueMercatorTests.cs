using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The ESRI WKT1 spelling of the oblique Mercator, and the skew angle that
/// spelling does not state (SpatialEngine-9r3).
/// <para>
/// ESRI names EPSG method 9812
/// <c>Hotine_Oblique_Mercator_Azimuth_Natural_Origin</c> — the name
/// <c>projinfo -o WKT1_ESRI</c> emits for EPSG:3078 — and that dialect has no
/// parameter for the angle from the rectified to the skew grid. EPSG's own
/// registry states one for the same grid (337.25556, the same number as the
/// azimuth), so a reader that handed ProjNet a skew angle of zero would serve
/// an unrotated grid under a skew-grid definition, and a parameter accepted
/// and ignored is what this repository does not do.
/// </para>
/// <para>
/// So the reader supplies it: the angle is the azimuth of the initial line,
/// which is PROJ's own reading of the dialect (<c>+gamma</c> defaults to
/// <c>+alpha</c> in <c>+proj=omerc</c>, and PROJ 9.8.1 imports the document
/// above to <c>+proj=omerc … +alpha=-22.74444 +gamma=-22.74444</c>). The
/// control points below are PROJ's output for that document, so the WKT1
/// spelling and the WKT2 of the same grid agree — to the fourth decimal of a
/// metre, which is the difference between EPSG's six-decimal false offsets as
/// the ESRI dialect states them and the extra digits its own registry gives.
/// A document in this dialect that states a skew angle anyway is refused by
/// name rather than read one way and served the other.
/// </para>
/// </summary>
public sealed class ProjWktEsriObliqueMercatorTests
{
    /// <summary>
    /// EPSG:3078 NAD83 / Michigan Oblique Mercator as <c>projinfo -o
    /// WKT1_ESRI</c> spells it: the ESRI WKT1 dialect, whose oblique Mercator
    /// states the azimuth and stops there.
    /// </summary>
    private const string MichiganEsriWkt1 = """
        PROJCS["NAD_1983_Michigan_GeoRef_Meters",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Hotine_Oblique_Mercator_Azimuth_Natural_Origin"],PARAMETER["False_Easting",2546731.496],PARAMETER["False_Northing",-4354009.816],PARAMETER["Scale_Factor",0.9996],PARAMETER["Azimuth",-22.74444],PARAMETER["Longitude_Of_Center",-86.0],PARAMETER["Latitude_Of_Center",45.3091666666667],UNIT["Meter",1.0]]
        """;

    /// <summary>
    /// The same document with the skew angle stated anyway — which PROJ 9.8.1
    /// reads and then ignores (it imports the document above and this one to
    /// the same proj string, <c>+gamma</c> equal to <c>+alpha</c> either
    /// way). The engine refuses it rather than serving it the way it would be
    /// served if the angle were honoured: a parameter is honoured or refused
    /// by name, never accepted and ignored.
    /// </summary>
    private const string MichiganEsriWkt1StatingASkewAngle =
        """
        PROJCS["NAD_1983_Michigan_GeoRef_Meters",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Hotine_Oblique_Mercator_Azimuth_Natural_Origin"],PARAMETER["False_Easting",2546731.496],PARAMETER["False_Northing",-4354009.816],PARAMETER["Scale_Factor",0.9996],PARAMETER["Azimuth",-22.74444],PARAMETER["Longitude_Of_Center",-86.0],PARAMETER["Latitude_Of_Center",45.3091666666667],PARAMETER["Angle_From_Rectified_To_Skew_Grid",-10.0],UNIT["Meter",1.0]]
        """;

    /// <summary>
    /// PROJ 9.8.1's own coordinates for the document above, read through
    /// pyproj with <c>always_xy</c> on the document's own ellipsoid and with
    /// no datum shift: the projection centre, which this grid's skew grid
    /// angle puts at its natural origin rather than on the false offsets, and
    /// four points across the Lower Peninsula.
    /// </summary>
    public static TheoryData<double, double, double, double> ProjControlPoints()
    {
        var data = new TheoryData<double, double, double, double>();

        data.Add(-86.0, 45.3091666666667, 499840.25238587055, 528600.3033698285);
        data.Add(-85.0, 45.5, 577967.3309175835, 550286.510574393);
        data.Add(-83.0, 44.0, 740335.1544894611, 387567.5326477429);
        data.Add(-87.5, 46.0, 383695.8067500545, 606443.3155174358);
        data.Add(-84.5, 43.5, 621114.0905215829, 328757.81460122485);

        return data;
    }

    /// <summary>
    /// The ESRI spelling resolves to the same ProjNet projection as EPSG's
    /// Hotine Oblique Mercator (variant A) — <c>Oblique_Mercator</c>, the
    /// variant A reading where the false offsets are applied at the natural
    /// origin — and the skew angle it does not state is read as the azimuth of
    /// the initial line, which is the number EPSG's registry states for this
    /// grid (337.25556, written -22.74444 in this dialect).
    /// </summary>
    [Fact]
    public void The_ESRI_spelling_resolves_to_the_variant_A_projection_with_the_skew_angle_it_omits()
    {
        Assert.True(ProjWkt.TryParse(MichiganEsriWkt1, out var definition, out var error), error);
        var projected = Assert.IsType<ProjectedDefinition>(definition);

        Assert.Equal("Oblique_Mercator", projected.ProjectionClass);
        Assert.Equal(-22.74444, projected.Parameters.Single(p => p.Name == "azimuth").Value, 9);
        Assert.Equal(
            -22.74444,
            projected.Parameters.Single(p => p.Name == "rectified_grid_angle").Value,
            9);
        Assert.Equal(45.3091666666667, projected.Parameters.Single(p => p.Name == "latitude_of_origin").Value, 9);
        Assert.Equal(-86.0, projected.Parameters.Single(p => p.Name == "central_meridian").Value, 9);
    }

    /// <summary>
    /// And the document is read as PROJ reads it: the centre lands on the
    /// natural origin, where EPSG puts it, rather than on the false offsets.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProjControlPoints))]
    public void The_ESRI_document_transforms_to_the_PROJ_coordinate(
        double longitude, double latitude, double easting, double northing)
    {
        var (x, y) = Project(MichiganEsriWkt1, longitude, latitude);

        Assert.Equal(easting, x, 6);
        Assert.Equal(northing, y, 6);
    }

    /// <summary>
    /// And inverted, from PROJ's own coordinates rather than from the
    /// engine's, so a wrong inverse cannot hide behind a wrong forward.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProjControlPoints))]
    public void The_ESRI_document_inverts_back_to_the_given_point(
        double longitude, double latitude, double easting, double northing)
    {
        var (backLongitude, backLatitude) = Inverse(MichiganEsriWkt1, easting, northing);

        Assert.Equal(longitude, backLongitude, 6);
        Assert.Equal(latitude, backLatitude, 6);
    }

    /// <summary>
    /// What the convention is worth, sized. A skew angle of zero — the
    /// unrotated grid, which is what a reader that maps this spelling and
    /// reads no angle serves — puts the projection centre 2,046,891 m east
    /// and 411,693 m north of where PROJ and EPSG put it, so every point of
    /// the definition inherits a 2,047 km error. A future change that stops
    /// supplying the angle has to move this number.
    /// </summary>
    [Fact]
    public void A_skew_angle_of_zero_is_two_thousand_kilometres_away()
    {
        var (easting, northing) = Direct(
            "Oblique_Mercator",
            [
                Parameter("latitude_of_origin", 45.30916666666666),
                Parameter("central_meridian", -86.0),
                Parameter("azimuth", 337.25556),
                Parameter("rectified_grid_angle", 0.0),
                Parameter("scale_factor", 0.9996),
                Parameter("false_easting", 2546731.496),
                Parameter("false_northing", -4354009.816),
            ],
            6378137.0,
            298.257222101,
            -86.0,
            45.30916666666666);

        Assert.InRange(easting - 499840.25238587055, 2_046_000.0, 2_048_000.0);
        Assert.InRange(northing - 528600.3033698285, 411_000.0, 412_000.0);
    }

    /// <summary>
    /// A document in this dialect that states a skew angle is refused by
    /// name. The dialect has no such parameter, PROJ ignores the one this
    /// engine would read, and serving the document either way would be
    /// choosing silently between two grids: the one the name and the azimuth
    /// describe, and the one the stated angle describes.
    /// </summary>
    [Fact]
    public void An_ESRI_document_stating_a_skew_angle_is_refused_by_name()
    {
        Assert.False(ProjWkt.TryParse(MichiganEsriWkt1StatingASkewAngle, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("Angle_From_Rectified_To_Skew_Grid", error, StringComparison.Ordinal);
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
        var projected = factory.CreateProjectedCoordinateSystem(
            "probe",
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

        var transform = new ProjTf.CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(Base(projectedDefinition.Base), Build(projectedDefinition)).MathTransform;

        return transform.Transform(longitude, latitude);
    }

    private static (double Longitude, double Latitude) Inverse(string wkt, double easting, double northing)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projectedDefinition = Assert.IsType<ProjectedDefinition>(definition);

        var transform = new ProjTf.CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(Build(projectedDefinition), Base(projectedDefinition.Base)).MathTransform;

        return transform.Transform(easting, northing);
    }

    private static ProjCs.ProjectedCoordinateSystem Build(ProjectedDefinition definition) =>
        ProjEpsgCatalog.Build(definition);

    /// <summary>
    /// The geographic CRS a definition projects from, built from the
    /// definition's own ellipsoid: the comparison here is the projection
    /// against PROJ, so the base is the same ellipsoid and nothing else.
    /// </summary>
    private static ProjCs.GeographicCoordinateSystem Base(GeodeticDefinition definition) =>
        Geographic(new ProjCs.CoordinateSystemFactory(), definition.SemiMajor, definition.InverseFlattening);
}
