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
    /// EPSG:29873 Timbalai 1948 / RSO Borneo, Hotine Oblique Mercator (variant
    /// B, EPSG method 9815) — the variant B definition that is not the Swiss
    /// one, and so the one that can see whether the oblique parameters are
    /// read at all. LV95's azimuth of initial line and angle from rectified to
    /// skew grid are both 90°, which is what ProjNet's Hotine defaults to, so
    /// a reader that quietly dropped either of them would reproduce LV95 to
    /// the last bit. Here they are 53.3158204722222° and 53.1301023611111°,
    /// different from each other and from every default, on an Everest
    /// ellipsoid nothing else in the catalogue uses, and EPSG spells the
    /// azimuth parameter "Azimuth at projection centre" rather than the Swiss
    /// "Azimuth of initial line" — two spellings of EPSG 8813, so both have
    /// to be read for the method to be usable on anything but Switzerland.
    /// </summary>
    private const string BorneoObliqueMercator = """
        PROJCRS["Timbalai 1948 / RSO Borneo (m)",BASEGEOGCRS["Timbalai 1948",DATUM["Timbalai 1948",ELLIPSOID["Everest 1830 (1967 Definition)",6377298.556,300.8017,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4298]],CONVERSION["Rectified Skew Orthomorphic Borneo Grid (metre)",METHOD["Hotine Oblique Mercator (variant B)",ID["EPSG",9815]],PARAMETER["Latitude of projection centre",4,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",115,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth at projection centre",53.3158204722222,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",53.1301023611111,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",0.99984,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["Easting at projection centre",590476.87,LENGTHUNIT["metre",1],ID["EPSG",8816]],PARAMETER["Northing at projection centre",442857.65,LENGTHUNIT["metre",1],ID["EPSG",8817]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",29873]]
        """;

    /// <summary>
    /// EPSG:3078 NAD83 / Michigan Oblique Mercator, Hotine Oblique Mercator
    /// (variant A, EPSG method 9812) — a variant-A definition, whose false
    /// offsets are applied at the natural origin rather than at the
    /// projection centre. Its offsets carry the extra digits the ESRI and
    /// NAD27-era definitions give (EPSG's own registry states them to six
    /// decimals), which is why PROJ's coordinates for it and for the ESRI
    /// WKT1 spelling of the same grid differ in the fourth decimal of a
    /// metre.
    /// </summary>
    private const string MichiganObliqueMercator = """
        PROJCRS["NAD83 / Michigan Oblique Mercator",BASEGEOGCRS["NAD83",DATUM["North American Datum 1983",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4269]],CONVERSION["Michigan Oblique Mercator (meter)",METHOD["Hotine Oblique Mercator (variant A)",ID["EPSG",9812]],PARAMETER["Latitude of projection centre",45.3091666666667,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",-86,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth of initial line",337.25556,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",337.25556,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",0.9996,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["Easting at projection centre",2546731.4967949,LENGTHUNIT["metre",1],ID["EPSG",8816]],PARAMETER["Northing at projection centre",-4354009.8168466,LENGTHUNIT["metre",1],ID["EPSG",8817]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3078]]
        """;

    /// <summary>
    /// EPSG:29874 Timbalai 1948 / RSO Sarawak LSD (m), Hotine Oblique
    /// Mercator (variant A) — the same datum and the same Everest ellipsoid
    /// as the Borneo variant B definition above, and almost the same oblique
    /// parameters, so a reader that treated the two variants as one would
    /// land these two grids on each other. Its false offsets are stated the
    /// way EPSG states them for variant A — "False easting" and "False
    /// northing", not the variant B "Easting at projection centre" — and
    /// they are 2,000 km and 5,000 km of it.
    /// </summary>
    private const string SarawakObliqueMercator = """
        PROJCRS["Timbalai 1948 / RSO Sarawak LSD (m)",BASEGEOGCRS["Timbalai 1948",DATUM["Timbalai 1948",ELLIPSOID["Everest 1830 (1967 Definition)",6377298.556,300.8017,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4298]],CONVERSION["Rectified Skew Orthomorphic Sarawak LSD (metre)",METHOD["Hotine Oblique Mercator (variant A)",ID["EPSG",9812]],PARAMETER["Latitude of projection centre",4,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",115,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth at projection centre",53.3158204722222,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",53.1301023611111,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",0.99984,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["False easting",2000000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",5000000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",29874]]
        """;

    /// <summary>
    /// ESRI:102544 OCRS Oregon Coast NAD 1983 CORS96 OM (metre), Hotine
    /// Oblique Mercator (variant A) — the 5°-azimuth case, whose projection
    /// centre is nowhere near the origin of the axes and whose offsets are
    /// both negative, so the false offsets cannot be read as "somewhere near
    /// (0, 0)" by a reader that has only seen positive ones.
    /// </summary>
    private const string OregonCoastObliqueMercator = """
        PROJCRS["OCRS_Oregon_Coast_NAD_1983_CORS96_OM_Meters",BASEGEOGCRS["NAD83(CORS96)",DATUM["NAD83 (Continuously Operating Reference Station 1996)",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",6783]],CONVERSION["OCRS_Oregon_Coast_NAD_1983_CORS96_OM_Meters",METHOD["Hotine Oblique Mercator (variant A)",ID["EPSG",9812]],PARAMETER["Latitude of projection centre",44.75,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",-124.05,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth at projection centre",5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",1,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["False easting",-300000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",-4600000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["ESRI",102544]]
        """;

    /// <summary>
    /// ESRI:102366 NAD 1983 CORS96 StatePlane Alaska 1 FIPS 5001, Hotine
    /// Oblique Mercator (variant A) — the 5,000 km of false offset in each
    /// axis, the largest the variant A definitions carry, so the translation
    /// the convention moves cannot be confused with the offset itself.
    /// </summary>
    private const string AlaskaObliqueMercator = """
        PROJCRS["NAD_1983_CORS96_StatePlane_Alaska_1_FIPS_5001",BASEGEOGCRS["NAD83(CORS96)",DATUM["NAD83 (Continuously Operating Reference Station 1996)",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",6783]],CONVERSION["NAD1983_CORS96_StatePlane_Alaska_1_FIPS_5001",METHOD["Hotine Oblique Mercator (variant A)",ID["EPSG",9812]],PARAMETER["Latitude of projection centre",57,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8811]],PARAMETER["Longitude of projection centre",-133.666666666667,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8812]],PARAMETER["Azimuth at projection centre",-36.8698976458333,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8813]],PARAMETER["Angle from Rectified to Skew Grid",-36.8698976458333,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8814]],PARAMETER["Scale factor at projection centre",0.9999,SCALEUNIT["unity",1],ID["EPSG",8815]],PARAMETER["False easting",5000000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",-5000000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["ESRI",102366]]
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
    /// EPSG:3032 WGS 84 / Australian Antarctic Polar Stereographic, Polar
    /// Stereographic (variant B, EPSG method 9829) — the same standard
    /// parallel as EPSG:3031 on a central meridian of 70°E, with 6,000 km of
    /// false offset in each axis, so the derivation of the scale factor and
    /// the longitude and the offsets are all read.
    /// </summary>
    private const string AustralianAntarcticPolarStereographic = """
        PROJCRS["WGS 84 / Australian Antarctic Polar Stereographic",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["Australian Antarctic Polar Stereographic",METHOD["Polar Stereographic (variant B)",ID["EPSG",9829]],PARAMETER["Latitude of standard parallel",-71,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8832]],PARAMETER["Longitude of origin",70,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["False easting",6000000,LENGTHUNIT["metre",1],ID["EPSG",8836]],PARAMETER["False northing",6000000,LENGTHUNIT["metre",1],ID["EPSG",8837]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3032]]
        """;

    /// <summary>
    /// EPSG:3413 WGS 84 / NSIDC Sea Ice Polar Stereographic North, Polar
    /// Stereographic (variant B, EPSG method 9829) — the Arctic case, whose
    /// standard parallel is 70°N rather than 71°S, so the pole the reader
    /// derives is the northern one and the sign of the northing follows.
    /// </summary>
    private const string SeaIcePolarStereographicNorth = """
        PROJCRS["WGS 84 / NSIDC Sea Ice Polar Stereographic North",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["US NSIDC Sea Ice polar stereographic north",METHOD["Polar Stereographic (variant B)",ID["EPSG",9829]],PARAMETER["Latitude of standard parallel",70,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8832]],PARAMETER["Longitude of origin",-45,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8836]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8837]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3413]]
        """;

    /// <summary>
    /// EPSG:3031 with the parameter the derivation reads left out, and
    /// EPSG:3031 with that parameter and a scale factor as well: a document
    /// that states neither or both says something the reader cannot honour,
    /// and says it by name.
    /// </summary>
    private const string AntarcticWithoutItsStandardParallel = """
        PROJCRS["WGS 84 / Antarctic Polar Stereographic",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["Antarctic Polar Stereographic",METHOD["Polar Stereographic (variant B)",ID["EPSG",9829]],PARAMETER["Longitude of origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8836]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8837]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3031]]
        """;

    private const string AntarcticWithBothScaleFactors = """
        PROJCRS["WGS 84 / Antarctic Polar Stereographic",BASEGEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]],CONVERSION["Antarctic Polar Stereographic",METHOD["Polar Stereographic (variant B)",ID["EPSG",9829]],PARAMETER["Latitude of standard parallel",-71,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8832]],PARAMETER["Scale factor at natural origin",0.9727690128917972,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["Longitude of origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8833]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8836]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8837]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3031]]
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
    /// The same LAEA Europe definition with the axes EPSG:5513 declares —
    /// (X) south, (Y) west — on a method the engine reads and has measured
    /// against PROJ. The axis rule is about the axes, not the method, so it
    /// is sized on a document whose method is not in question: read, this
    /// would be a CRS whose description says easting and northing and whose
    /// numbers are southings and westings.
    /// </summary>
    private const string LaeaEuropeDeclaredSouthWest = """
        PROJCRS["ETRS89-extended / LAEA Europe",BASEGEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4258]],CONVERSION["Europe Equal Area 2001",METHOD["Lambert Azimuthal Equal Area",ID["EPSG",9820]],PARAMETER["Latitude of natural origin",52,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",10,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["False easting",4321000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",3210000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(X)",south,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(Y)",west,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3035]]
        """;

    /// <summary>
    /// The same document with the axes transposed — (Y) north first, (X) east
    /// second — which is the other way a document can declare axes the engine
    /// does not serve, and the one a swap would fix. It is refused for the
    /// same reason, because the engine's answer is to refuse rather than to
    /// swap.
    /// </summary>
    private const string LaeaEuropeDeclaredNorthEast = """
        PROJCRS["ETRS89-extended / LAEA Europe",BASEGEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4258]],CONVERSION["Europe Equal Area 2001",METHOD["Lambert Azimuthal Equal Area",ID["EPSG",9820]],PARAMETER["Latitude of natural origin",52,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",10,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["False easting",4321000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",3210000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(Y)",north,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(X)",east,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3035]]
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
    [InlineData(BorneoObliqueMercator, "Hotine Oblique Mercator (variant B)", "Hotine_Oblique_Mercator")]
    [InlineData(MichiganObliqueMercator, "Hotine Oblique Mercator (variant A)", "Oblique_Mercator")]
    [InlineData(SarawakObliqueMercator, "Hotine Oblique Mercator (variant A)", "Oblique_Mercator")]
    [InlineData(OregonCoastObliqueMercator, "Hotine Oblique Mercator (variant A)", "Oblique_Mercator")]
    [InlineData(AlaskaObliqueMercator, "Hotine Oblique Mercator (variant A)", "Oblique_Mercator")]
    [InlineData(AntarcticPolarStereographic, "Polar Stereographic (variant B)", "Polar_Stereographic")]
    [InlineData(AustralianAntarcticPolarStereographic, "Polar Stereographic (variant B)", "Polar_Stereographic")]
    [InlineData(SeaIcePolarStereographicNorth, "Polar Stereographic (variant B)", "Polar_Stereographic")]
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
    /// three points in Switzerland on the LV95 parameters. The polar
    /// stereographic variant B points are the two hemispheres and the
    /// 6,000 km of false offset, because the scale factor the reader derives
    /// from the standard parallel is what puts them where PROJ puts them.
    /// The oblique Mercator variant A points are four grids — Michigan,
    /// Sarawak, the Oregon Coast and south-east Alaska — whose false offsets
    /// run from both-negative to 5,000 km in each axis, because where those
    /// offsets are applied is the whole of what separates the two variants.
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

        // EPSG:29873, Timbalai 1948 / RSO Borneo — the variant B definition
        // whose oblique parameters are not ProjNet's defaults.
        data.Add(BorneoObliqueMercator, 115.0, 5.0, 590121.1779573819, 553415.8095151458);
        data.Add(BorneoObliqueMercator, 116.0, 6.0, 700491.1134436313, 664407.7098906768);
        data.Add(BorneoObliqueMercator, 114.0, 4.0, 479457.4987435189, 442562.66950517416);
        data.Add(BorneoObliqueMercator, 117.0, 7.0, 810510.9238337873, 775563.6956500097);

        // EPSG:3031, WGS 84 / Antarctic Polar Stereographic.
        data.Add(AntarcticPolarStereographic, 0.0, -80.0, 0.0, 1089179.4556261837);
        data.Add(AntarcticPolarStereographic, 30.0, -75.0, 819391.6192036181, 1419227.9157567972);
        data.Add(AntarcticPolarStereographic, -45.0, -72.0, -1393947.5396750527, 1393947.5396750532);
        data.Add(AntarcticPolarStereographic, 179.0, -85.0, 9487.011175154154, -543510.5062151697);

        // EPSG:3032, WGS 84 / Australian Antarctic Polar Stereographic — the
        // 6,000 km of false offset, which a derived scale factor must not
        // disturb.
        data.Add(AustralianAntarcticPolarStereographic, 70.0, -80.0, 6000000.0, 7089179.455626184);
        data.Add(AustralianAntarcticPolarStereographic, 120.0, -75.0, 7255380.793258387, 7053389.560610154);
        data.Add(AustralianAntarcticPolarStereographic, 150.0, -72.0, 7941390.439023555, 6342319.514489304);
        data.Add(AustralianAntarcticPolarStereographic, 0.0, -85.0, 5489189.389059778, 6185919.857729575);

        // EPSG:3413, WGS 84 / NSIDC Sea Ice Polar Stereographic North — the
        // northern pole, where the northing is negative off the central
        // meridian.
        data.Add(SeaIcePolarStereographicNorth, -45.0, 80.0, 0.0, -1085920.2973930992);
        data.Add(SeaIcePolarStereographicNorth, -15.0, 75.0, 816939.7487353927, -1414981.1515322526);
        data.Add(SeaIcePolarStereographicNorth, -60.0, 72.0, -508693.4760589377, -1898469.8981307785);
        data.Add(SeaIcePolarStereographicNorth, -45.0, 89.0, 0.0, -108329.9596389848);

        // EPSG:3078, NAD83 / Michigan Oblique Mercator — variant A, whose
        // projection centre is at (499840.2532, 528600.3025) and not on the
        // false easting and northing.
        data.Add(MichiganObliqueMercator, -86.0, 45.30916666666666, 499840.25318077067, 528600.3025232237);
        data.Add(MichiganObliqueMercator, -85.0, 45.5, 577967.3317124837, 550286.5097277928);
        data.Add(MichiganObliqueMercator, -83.0, 44.0, 740335.1552843612, 387567.53180114273);
        data.Add(MichiganObliqueMercator, -87.5, 46.0, 383695.8075449546, 606443.3146708356);
        data.Add(MichiganObliqueMercator, -84.5, 43.5, 621114.091316483, 328757.8137546247);

        // EPSG:29874, Timbalai 1948 / RSO Sarawak LSD — variant A on the
        // Everest ellipsoid, on the same oblique parameters as the Borneo
        // variant B definition above and 5,000 km of false northing.
        data.Add(SarawakObliqueMercator, 110.35, 1.53, 2073943.2630482286, 5169163.515648381);
        data.Add(SarawakObliqueMercator, 111.5, 2.2, 2201856.689242917, 5243254.074131392);
        data.Add(SarawakObliqueMercator, 112.5, 3.0, 2312986.25057334, 5331764.441868048);
        data.Add(SarawakObliqueMercator, 114.0, 4.5, 2479343.4127676655, 5497845.229681412);

        // ESRI:102544, OCRS Oregon Coast — variant A with both offsets
        // negative, so the origin of the axes is nowhere near the
        // projection centre in either direction.
        data.Add(OregonCoastObliqueMercator, -124.03, 44.45, 136335.2694881804, 335802.03086002637);
        data.Add(OregonCoastObliqueMercator, -124.5, 44.2, 98771.98987737397, 308120.80072116293);
        data.Add(OregonCoastObliqueMercator, -123.9, 44.6, 146652.35913570295, 352481.15665734746);
        data.Add(OregonCoastObliqueMercator, -124.4, 45.0, 107146.92354373506, 396980.9767032238);

        // ESRI:102366, StatePlane Alaska 1 — variant A with 5,000 km of
        // false offset in each axis, and a negative azimuth.
        data.Add(AlaskaObliqueMercator, -133.6666666666667, 57.0, 818676.7335827854, 575097.6885584872);
        data.Add(AlaskaObliqueMercator, -132.0, 58.0, 917223.3000086262, 687673.6321787294);
        data.Add(AlaskaObliqueMercator, -135.0, 56.0, 735492.0077300686, 464556.91681919154);
        data.Add(AlaskaObliqueMercator, -134.5, 59.0, 770757.1318007791, 798129.3967899391);

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

        // Six decimal places of a metre, i.e. a micrometre: the tolerance is
        // the documented agreement, not a loose bound. Every method in the
        // map is in fact closer than this — the largest deviation measured
        // across the control points above is 3e-9 m — so this is a claim the
        // numbers keep rather than one they merely clear.
        Assert.Equal(easting, x, 6);
        Assert.Equal(northing, y, 6);
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
    [InlineData(Krovak, "Krovak")]
    public void A_method_that_diverges_from_PROJ_is_refused_by_name(string wkt, string method)
    {
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains(method, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a Hotine variant A definition's false offsets are applied.
    /// EPSG applies them at the *natural origin* and Snyder's Alternate B —
    /// which is what ProjNet's <c>Hotine_Oblique_Mercator</c> computes —
    /// applies them at the *projection centre*, so that the centre lands on
    /// the false easting and northing exactly. The two are one translation
    /// apart, and it is 2,047 km on the Michigan grid: the projection centre
    /// is 2,046,891 m west and 4,882,610 m north of where PROJ puts it under
    /// the variant A reading, and so is every other point of the definition.
    /// <para>
    /// The reader resolves variant A to <c>Oblique_Mercator</c>, ProjNet's
    /// projection that does apply the offsets at the natural origin
    /// (ADR-0171), and this pins what that is worth: PROJ 9.8.1 puts the
    /// centre at (499840.25318077067, 528600.3025232237) on this definition's
    /// parameters, and the engine now puts it there to 1e-9 m.
    /// </para>
    /// </summary>
    [Fact]
    public void The_Hotine_variant_A_false_offsets_are_applied_at_the_natural_origin()
    {
        IReadOnlyCollection<ProjCs.ProjectionParameter> Parameters() =>
        [
            Parameter("latitude_of_center", 45.30916666666666),
            Parameter("longitude_of_center", -86.0),
            Parameter("azimuth", 337.25556),
            Parameter("rectified_grid_angle", 337.25556),
            Parameter("scale_factor", 0.9996),
            Parameter("false_easting", 2546731.4967949),
            Parameter("false_northing", -4354009.8168466),
        ];

        var (centreEasting, centreNorthing) = Direct(
            "Oblique_Mercator", Parameters(), 6378137.0, 298.257222101, -86.0, 45.30916666666666);

        // PROJ 9.8.1, on the same definition, applying the offsets at the
        // natural origin.
        Assert.Equal(499840.25318077067, centreEasting, 6);
        Assert.Equal(528600.3025232237, centreNorthing, 6);

        // The Alternate B reading puts the centre on the offsets themselves,
        // which is 2,046,891 m east and 4,882,610 m south of PROJ's centre.
        var (alternateBEasting, alternateBNorthing) = Direct(
            "Hotine_Oblique_Mercator", Parameters(), 6378137.0, 298.257222101, -86.0, 45.30916666666666);

        Assert.Equal(2546731.4967949, alternateBEasting, 6);
        Assert.Equal(-4354009.8168466, alternateBNorthing, 6);
        Assert.InRange(alternateBEasting - centreEasting, 2_046_000.0, 2_048_000.0);
        Assert.InRange(alternateBNorthing - centreNorthing, -4_884_000.0, -4_882_000.0);
    }

    /// <summary>
    /// The polar stereographic variant B derivation. ProjNet's polar
    /// stereographic is the variant A formulation — a pole, a scale factor and
    /// the false offsets — and it agrees with PROJ there to well under a
    /// micrometre. What EPSG's variant B states instead is a latitude of
    /// standard parallel, so the reader derives the two variant A parameters
    /// from it, on the definition's own ellipsoid and by PROJ's own
    /// expression: the scale factor is 0.9727690128917972 for EPSG:3031 and
    /// 0.9698581903263522 for EPSG:3413 (PROJ 9.8.1's own value for each,
    /// recovered by handing ProjNet the variant A parameters and reading the
    /// multiplier back).
    /// </summary>
    [Theory]
    [InlineData(AntarcticPolarStereographic, -90.0, 0.9727690128917972)]
    [InlineData(SeaIcePolarStereographicNorth, 90.0, 0.9698581903263522)]
    public void The_polar_stereographic_variant_B_scale_factor_is_derived_from_the_standard_parallel(
        string wkt, double pole, double scaleFactor)
    {
        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        var projected = Assert.IsType<ProjectedDefinition>(definition);

        Assert.Equal("Polar_Stereographic", projected.ProjectionClass);
        Assert.Equal(pole, projected.Parameters.Single(p => p.Name == "latitude_of_origin").Value, 9);
        Assert.Equal(scaleFactor, projected.Parameters.Single(p => p.Name == "scale_factor").Value, 12);

        // The standard parallel is not handed on as a parameter the
        // projection does not read: it has been turned into the scale factor,
        // so it is gone rather than quietly ignored.
        Assert.DoesNotContain(projected.Parameters, parameter => parameter.Name == "latitude_of_standard_parallel");
    }

    /// <summary>
    /// And the size of what the derivation is for. Handed the variant A
    /// parameters the reader derives, ProjNet reproduces EPSG:3031; handed
    /// the definition as a plain polar stereographic it does not, which is
    /// the 527 km of northing that kept the method out of the map.
    /// </summary>
    [Fact]
    public void The_polar_stereographic_variant_B_derivation_is_worth_527_km_of_northing()
    {
        var (easting, northing) = Direct(
            "Polar_Stereographic",
            [
                Parameter("latitude_of_origin", -90.0),
                Parameter("central_meridian", 0.0),
                Parameter("scale_factor", 0.9727690128917972),
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

        // Left to its own parameters — a scale factor of 1 at the pole — the
        // projection lands 527 km away.
        Assert.InRange(1089179.4556261837 - 561713.6916, 527_000.0, 528_000.0);
    }

    /// <summary>
    /// A parameter is honoured or refused by name, never accepted and
    /// ignored, and a variant B conversion states the parallel the derivation
    /// reads: a document that omits it, or that also states the variant A
    /// scale factor, is a named failure rather than a projection built from
    /// half a definition.
    /// </summary>
    [Fact]
    public void A_polar_stereographic_variant_B_definition_without_the_standard_parallel_is_refused_by_name()
    {
        Assert.False(ProjWkt.TryParse(AntarcticWithoutItsStandardParallel, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("latitude of standard parallel", error, StringComparison.OrdinalIgnoreCase);

        Assert.False(ProjWkt.TryParse(AntarcticWithBothScaleFactors, out definition, out error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("scale factor", error, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// The consequence of that axis convention, and the decision it forced
    /// (SpatialEngine-ufn): the engine's geometry is x-first easting and
    /// northing for every projected CRS, and the builder and <c>Describe</c>
    /// say easting and northing whatever the document declares. So a
    /// definition whose own axes are not easting and northing is refused by
    /// name, on any method — including one already in the map — rather than
    /// served as a CRS whose description contradicts its numbers.
    /// </summary>
    [Theory]
    [InlineData(LaeaEuropeDeclaredSouthWest, "south", "west")]
    [InlineData(LaeaEuropeDeclaredNorthEast, "north", "east")]
    public void A_projected_definition_declaring_axes_the_engine_does_not_serve_is_refused_by_name(
        string wkt, string firstAxis, string secondAxis)
    {
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains(firstAxis, error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondAxis, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the same EPSG:5513 document with its method mapped to one the
    /// engine reads, so that the axes are the only thing left refusing it:
    /// Krovak stays out for two independent reasons, and the second is the
    /// one that would survive a widening of the method map.
    /// </summary>
    [Fact]
    public void The_Krovak_axes_refuse_the_definition_whatever_method_it_names()
    {
        var wkt = Krovak.Replace(
            "METHOD[\"Krovak\",ID[\"EPSG\",9819]]",
            "METHOD[\"Polar Stereographic (variant A)\",ID[\"EPSG\",9810]]",
            StringComparison.Ordinal);

        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("Krovak", error, StringComparison.Ordinal);
        Assert.Contains("south", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("west", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The documents that state no axes at all are the WKT1 dialect's
    /// shorthand for easting and northing, which is what the engine serves,
    /// so the rule must not refuse them — the vendored catalogue is full of
    /// them.
    /// </summary>
    [Fact]
    public void A_projected_definition_that_states_no_axes_is_read_as_easting_and_northing()
    {
        const string wkt = """
            PROJCS["WGS 84 / World Mercator",GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Mercator_1SP"],PARAMETER["latitude_of_origin",0],PARAMETER["central_meridian",0],PARAMETER["scale_factor",1],PARAMETER["false_easting",0],PARAMETER["false_northing",0],UNIT["metre",1],AUTHORITY["EPSG","3395"]]
            """;

        Assert.True(ProjWkt.TryParse(wkt, out var definition, out var error), error);
        Assert.Equal("Mercator_1SP", Assert.IsType<ProjectedDefinition>(definition).ProjectionClass);
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
