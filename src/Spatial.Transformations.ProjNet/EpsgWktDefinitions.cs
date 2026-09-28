using System.Globalization;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The vendored EPSG definitions of the catalogue, as WKT (ADR-0027). The
/// WKT text is the data; <see cref="ProjWkt"/> reads it into a
/// <see cref="CrsDefinition"/> and <see cref="ProjEpsgCatalog"/> builds every
/// CRS through the one programmatic builder, so a definition is added by
/// adding its WKT — no parameter list to hand-write and no new construction
/// path to trust.
/// <para>
/// The definitions are the EPSG WKT2:2015 documents for each code, with one
/// deliberate addition: the catalogue carries the datum's shift to WGS84 in a
/// <c>TOWGS84</c> node, which WKT1 defined and the WKT2 documents omit. It is
/// the honest, grid-free approximation ADR-0027 §accuracy requires — zero for
/// the modern datums, the classic Helmert for OSGB36.
/// </para>
/// EPSG Geodetic Parameter Dataset © IOGP/EPSG (subset reproduced here under
/// the EPSG terms of use; see ADR-0027).
/// </summary>
internal static class EpsgWktDefinitions
{
    /// <summary>The WGS 84 base, the datum of the Web Mercator and UTM definitions below.</summary>
    private const string Wgs84 = """
        GEOGCRS["WGS 84",DATUM["World Geodetic System 1984",ELLIPSOID["WGS 84",6378137,298.257223563,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4326]]
        """;

    private const string Etrs89 = """
        GEOGCRS["ETRS89",DATUM["European Terrestrial Reference System 1989",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4258]]
        """;

    private const string Nad83 = """
        GEOGCRS["NAD83",DATUM["North American Datum 1983",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4269]]
        """;

    private const string OsGb36 = """
        GEOGCRS["OSGB36",DATUM["Ordnance Survey of Great Britain 1936",ELLIPSOID["Airy 1830",6377563.396,299.3249646,LENGTHUNIT["metre",1]],TOWGS84[446.448,-125.157,542.06,0.15,0.247,0.842,-20.489]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4277]]
        """;

    private const string Rgf93V1 = """
        GEOGCRS["RGF93 v1",DATUM["Reseau Geodesique Francais 1993",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",4171]]
        """;

    private const string NzGd2000 = """
        GEOGCRS["NZGD2000",DATUM["New Zealand Geodetic Datum 2000",ELLIPSOID["GRS 1980",6378137,298.257222101,LENGTHUNIT["metre",1]]],PRIMEM["Greenwich",0,ANGLEUNIT["degree",0.0174532925199433]],CS[ellipsoidal,2],AXIS["geodetic latitude (Lat)",north,ORDER[1],ANGLEUNIT["degree",0.0174532925199433]],AXIS["geodetic longitude (Lon)",east,ORDER[2],ANGLEUNIT["degree",0.0174532925199433]],ID["EPSG",2193]]
        """;

    /// <summary>
    /// EPSG:3857. The projection method is the one the reader intercepts
    /// (EPSG calls it "Popular Visualisation Pseudo Mercator"): Web Mercator
    /// is a spherical development of the ellipsoid, not a Mercator on it, and
    /// reading it as a plain Mercator costs 33 km of northing at Berlin —
    /// pinned by <c>ProjNetWktCatalogTests</c>.
    /// </summary>
    private const string WebMercator = """
        PROJCRS["WGS 84 / Pseudo-Mercator",BASEGEOGCRS{base},CONVERSION["Popular Visualisation Pseudo-Mercator",METHOD["Popular Visualisation Pseudo Mercator",ID["EPSG",1024]],PARAMETER["Latitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3857]]
        """;

    /// <summary>EPSG:3395, a plain Mercator on the ellipsoid — the same parameters as 3857 with a different projection.</summary>
    private const string WorldMercator = """
        PROJCRS["WGS 84 / World Mercator",BASEGEOGCRS{base},CONVERSION["World Mercator",METHOD["Mercator (variant A)",ID["EPSG",9804]],PARAMETER["Latitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["Scale factor at natural origin",1,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["False easting",0,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",0,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",3395]]
        """;

    private const string BritishNationalGrid = """
        PROJCRS["OSGB36 / British National Grid",BASEGEOGCRS{base},CONVERSION["British National Grid",METHOD["Transverse Mercator",ID["EPSG",9807]],PARAMETER["Latitude of natural origin",49,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",-2,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["Scale factor at natural origin",0.9996012717,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["False easting",400000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",-100000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",27700]]
        """;

    private const string Lambert93 = """
        PROJCRS["RGF93 v1 / Lambert-93",BASEGEOGCRS{base},CONVERSION["Lambert-93",METHOD["Lambert Conformal Conic (2SP)",ID["EPSG",9801]],PARAMETER["Latitude of false origin",46.5,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8821]],PARAMETER["Longitude of false origin",3,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8822]],PARAMETER["Latitude of 1st standard parallel",49,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8823]],PARAMETER["Latitude of 2nd standard parallel",44,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8824]],PARAMETER["Easting at false origin",700000,LENGTHUNIT["metre",1],ID["EPSG",8826]],PARAMETER["Northing at false origin",6600000,LENGTHUNIT["metre",1],ID["EPSG",8827]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",2154]]
        """;

    /// <summary>
    /// EPSG:2193, New Zealand Transverse Mercator: the UTM band over its own
    /// datum, with the false easting New Zealand's own national grid uses.
    /// </summary>
    private const string NewZealandTransverseMercator = """
        PROJCRS["NZGD2000 / New Zealand Transverse Mercator",BASEGEOGCRS{base},CONVERSION["New Zealand Transverse Mercator",METHOD["Transverse Mercator",ID["EPSG",9807]],PARAMETER["Latitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",173,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["Scale factor at natural origin",0.9996,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["False easting",1600000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",10000000,LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",2193]]
        """;

    /// <summary>
    /// The UTM zone template: the six-degree band parameters every zone
    /// shares, with the two that follow from the zone number ({@code
    /// central_meridian} and {@code false_northing}) as tokens, so a family
    /// member is this document with two numbers substituted.
    /// </summary>
    public const string UtmZone = """
        PROJCRS["{name}",BASEGEOGCRS{base},CONVERSION["UTM zone {zone}{hemisphere}",METHOD["Transverse Mercator",ID["EPSG",9807]],PARAMETER["Latitude of natural origin",0,ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8801]],PARAMETER["Longitude of natural origin",{central_meridian},ANGLEUNIT["degree",0.0174532925199433],ID["EPSG",8802]],PARAMETER["Scale factor at natural origin",0.9996,SCALEUNIT["unity",1],ID["EPSG",8805]],PARAMETER["False easting",500000,LENGTHUNIT["metre",1],ID["EPSG",8806]],PARAMETER["False northing",{false_northing},LENGTHUNIT["metre",1],ID["EPSG",8807]]],CS[Cartesian,2],AXIS["(E)",east,ORDER[1],LENGTHUNIT["metre",1]],AXIS["(N)",north,ORDER[2],LENGTHUNIT["metre",1]],ID["EPSG",{code}]]
        """;

    /// <summary>The geographic definitions, by EPSG code.</summary>
    public static IEnumerable<(int Code, string Wkt)> Geographic =>
    [
        (4326, Wgs84),
        (4258, Etrs89),
        (4269, Nad83),
        (4277, OsGb36),
        (4171, Rgf93V1),
        (4167, NzGd2000),
    ];

    /// <summary>
    /// The projected definitions that stand on their own, by EPSG code: the
    /// template, and the geodetic definition it names as its base.
    /// </summary>
    public static IEnumerable<(int Code, string Template, string BaseWkt)> Projected =>
    [
        (3857, WebMercator, Wgs84),
        (3395, WorldMercator, Wgs84),
        (27700, BritishNationalGrid, OsGb36),
        (2154, Lambert93, Rgf93V1),
        (2193, NewZealandTransverseMercator, NzGd2000),
    ];

    /// <summary>
    /// The projected families: CRSs that differ only by zone number, so one
    /// WKT template and the tokens a zone implies. <see cref="Family.BaseWkt"/>
    /// is the family's geodetic datum, <see cref="Family.NameFormat"/> the CRS
    /// name (zone number and hemisphere letter), and the codes run
    /// consecutively with the zones.
    /// </summary>
    public static IEnumerable<Family> Families =>
    [
        new("WGS 84 / UTM zone {0}{1}", Wgs84, 1, 60, 32601, "N"),
        new("WGS 84 / UTM zone {0}{1}", Wgs84, 1, 60, 32701, "S"),
        new("ETRS89 / UTM zone {0}{1}", Etrs89, 28, 38, 25828, "N"),
        new("NAD83 / UTM zone {0}{1}", Nad83, 1, 23, 26901, "N"),
    ];

    /// <summary>A family of projected CRSs generated from one WKT template.</summary>
    public sealed record Family(
        string NameFormat,
        string BaseWkt,
        int FirstZone,
        int LastZone,
        int FirstCode,
        string Hemisphere);

    /// <summary>
    /// The tokens one projected template needs: the geodetic definition it
    /// names as its base, re-labelled as a {@code BASEGEOGCRS}, and whatever
    /// the definition varies by code.
    /// </summary>
    public static Dictionary<string, string> Tokens(string baseWkt, params (string Name, string Value)[] values)
    {
        var tokens = new Dictionary<string, string>(values.Length + 1) { ["base"] = BodyOf(baseWkt) };
        foreach (var (name, value) in values)
        {
            tokens[name] = value;
        }

        return tokens;
    }

    /// <summary>
    /// The tokens one zone of a family supplies. The band parameters follow the
    /// UTM grid's own rule — a central meridian of <c>6 * zone - 183</c>
    /// degrees, and a 10,000,000 m false northing south of the equator — and
    /// are written round-tripped, so the text carries back exactly the double
    /// the rule computed.
    /// </summary>
    public static Dictionary<string, string> ZoneTokens(Family family, int zone, int code) => Tokens(
        family.BaseWkt,
        ("name", string.Format(CultureInfo.InvariantCulture, family.NameFormat, zone, family.Hemisphere)),
        ("zone", zone.ToString(CultureInfo.InvariantCulture)),
        ("hemisphere", family.Hemisphere),
        ("central_meridian", Round(6.0 * zone - 183.0)),
        ("false_northing", Round(family.Hemisphere == "N" ? 0.0 : 10_000_000.0)),
        ("code", code.ToString(CultureInfo.InvariantCulture)));

    /// <summary>A round-trippable rendering of a double, so a template's text parses back to the same double.</summary>
    private static string Round(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// A geodetic document's body — everything inside its root brackets, so
    /// it can be wrapped in a {@code BASEGEOGCRS} node: a projected CRS names
    /// its base with the same definition, not a copy of it.
    /// </summary>
    private static string BodyOf(string geodeticWkt) => geodeticWkt[geodeticWkt.IndexOf('[')..];
}
