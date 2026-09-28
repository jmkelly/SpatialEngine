using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ProjCs = ProjNet.CoordinateSystems;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The EPSG catalogue of the transformation provider (ADR-0027): common
/// geographic and projected CRSs plus the projected families generated from
/// a parameter template, all built programmatically through ProjNet's
/// factory. The catalogue is deliberately programmatic rather than
/// WKT-parsed: ProjNet 2.1's WKT reader maps the
/// "Popular Visualisation Pseudo-Mercator" projection class to a plain
/// Mercator_1SP (a ~33 km northing distortion on Web Mercator), so parsing
/// EPSG WKT at runtime is not a reliable construction path. Each
/// definition carries its datum-to-WGS84 shift ("TOWGS84"): zero for modern
/// datums, the classic Helmert approximation for OSGB36 (the WKT1 library
/// has no grid support — ADR-0027 §accuracy).
/// <para>
/// Instances that differ only by one zone number are generated, not typed
/// out: the UTM families (zones 1-60 north and south, and the ETRS89 and
/// NAD83 bands that use the same Transverse Mercator parameter set with a
/// different geodetic datum) come from one template each, so every zone a
/// client asks for is served and no served code depends on a hand-written
/// row. The dictionary is built once and every coordinate system in it is
/// built on first use, so generating a family costs one dictionary entry per
/// zone rather than a coordinate system.
/// </para>
///
/// EPSG Geodetic Parameter Dataset © IOGP/EPSG (subset reproduced here under
/// the EPSG terms of use; see ADR-0027).
/// </summary>
internal static class ProjEpsgCatalog
{
    /// <summary>Looks up a CRS definition by EPSG code.</summary>
    public static bool TryGet(int code, [NotNullWhen(true)] out ProjCs.CoordinateSystem? coordinateSystem)
    {
        if (Entries.TryGetValue(code, out var entry))
        {
            coordinateSystem = entry.Value;
            return true;
        }

        coordinateSystem = null;
        return false;
    }

    /// <summary>The EPSG codes served by this catalogue, generated families included.</summary>
    public static IEnumerable<int> Codes => Entries.Keys;

    /// <summary>Test pin: whether a code's coordinate system has been built yet (the catalogue builds each one once, on first use).</summary>
    public static bool IsBuilt(int code) =>
        Entries.TryGetValue(code, out var entry) && entry.IsValueCreated;

    private sealed record GeographicEntry(
        int Code,
        string Name,
        string DatumName,
        string EllipsoidName,
        double SemiMajor,
        double InverseFlattening,
        double[] ToWgs84);

    private sealed record ProjectedEntry(
        int Code,
        string Name,
        int GeodeticCode,
        string ProjectionClass,
        (string Name, double Value)[] Parameters);

    /// <summary>
    /// A family of projected CRSs that differ only by zone number: one
    /// geodetic datum, one projection, and the six-degree band parameters
    /// every UTM zone shares (a central meridian of <c>6 * zone - 183</c>
    /// degrees, scale 0.9996, a 500,000 m false easting, and a false
    /// northing of 0 north of the equator / 10,000,000 m south of it).
    /// <paramref name="NameFormat"/> is the CRS-name format, taking the zone
    /// number and the hemisphere letter. <paramref name="FirstCode"/> is the EPSG
    /// code of <paramref name="FirstZone"/>,
    /// and the codes run consecutively with the zones (EPSG numbers UTM
    /// zones 326xx north and 327xx south, ETRS89 zones 258xx, NAD83 zones
    /// 269xx).
    /// </summary>
    private sealed record ProjectedFamily(
        string NameFormat,
        int GeodeticCode,
        int FirstZone,
        int LastZone,
        int FirstCode,
        bool Northern);

    private static readonly GeographicEntry[] Geographic =
    [
        new(4326, "WGS 84", "World Geodetic System 1984", "WGS 84", 6378137.0, 298.257223563, [0, 0, 0, 0, 0, 0, 0]),
        new(4258, "ETRS89", "European Terrestrial Reference System 1989", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0]),
        new(4269, "NAD83", "North American Datum 1983", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0]),
        new(4277, "OSGB36", "Ordnance Survey of Great Britain 1936", "Airy 1830", 6377563.396, 299.3249646, [446.448, -125.157, 542.060, 0.15, 0.247, 0.842, -20.489]),
        new(4171, "RGF93 v1", "Reseau Geodesique Francais 1993", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0]),
    ];

    private static readonly ProjectedEntry[] Projected =
    [
        new(3857, "WGS 84 / Pseudo-Mercator", 4326, "Popular Visualisation Pseudo-Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 0.0), ("false_easting", 0.0), ("false_northing", 0.0)]),
        new(27700, "OSGB36 / British National Grid", 4277, "Transverse_Mercator", [("latitude_of_origin", 49.0), ("central_meridian", -2.0), ("scale_factor", 0.9996012717), ("false_easting", 400000.0), ("false_northing", -100000.0)]),
        new(2154, "RGF93 v1 / Lambert-93", 4171, "Lambert_Conformal_Conic_2SP", [("latitude_of_origin", 46.5), ("central_meridian", 3.0), ("standard_parallel_1", 49.0), ("standard_parallel_2", 44.0), ("false_easting", 700000.0), ("false_northing", 6600000.0)]),
    ];

    /// <summary>
    /// The UTM families: the world grid over all sixty zones in both
    /// hemispheres, and the ETRS89 and NAD83 bands, which use the same
    /// parameter set over their own datums. Every zone these produce was
    /// previously either hand-written (7 of them) or unserved.
    /// </summary>
    private static readonly ProjectedFamily[] Families =
    [
        new("WGS 84 / UTM zone {0}{1}", 4326, 1, 60, 32601, true),
        new("WGS 84 / UTM zone {0}{1}", 4326, 1, 60, 32701, false),
        new("ETRS89 / UTM zone {0}{1}", 4258, 28, 38, 25828, true),
        new("NAD83 / UTM zone {0}{1}", 4269, 1, 23, 26901, true),
    ];

    private static Dictionary<int, Lazy<ProjCs.CoordinateSystem>> BuildEntries()
    {
        var entries = new Dictionary<int, Lazy<ProjCs.CoordinateSystem>>();
        foreach (var entry in Geographic)
        {
            entries[entry.Code] = new Lazy<ProjCs.CoordinateSystem>(() => BuildGeographic(entry));
        }

        foreach (var entry in Projected)
        {
            entries[entry.Code] = new Lazy<ProjCs.CoordinateSystem>(() => BuildProjected(entry));
        }

        foreach (var family in Families)
        {
            AddFamily(entries, family);
        }

        return entries;
    }

    /// <summary>
    /// Adds one zone of a family. The zone's code, name and parameters are
    /// captured per iteration, so each entry is built from its own zone and
    /// stays a lazy single build like a hand-written row.
    /// </summary>
    private static void AddFamily(Dictionary<int, Lazy<ProjCs.CoordinateSystem>> entries, ProjectedFamily family)
    {
        for (var zone = family.FirstZone; zone <= family.LastZone; zone++)
        {
            var entry = UtmZone(family, zone);
            entries[entry.Code] = new Lazy<ProjCs.CoordinateSystem>(() => BuildProjected(entry));
        }
    }

    /// <summary>One zone of a UTM family, with the band's standard Transverse Mercator parameters.</summary>
    private static ProjectedEntry UtmZone(ProjectedFamily family, int zone)
    {
        var code = family.FirstCode + (zone - family.FirstZone);
        var hemisphere = family.Northern ? "N" : "S";
        return new ProjectedEntry(
            code,
            string.Format(CultureInfo.InvariantCulture, family.NameFormat, zone, hemisphere),
            family.GeodeticCode,
            "Transverse_Mercator",
            [
                ("latitude_of_origin", 0.0),
                ("central_meridian", 6.0 * zone - 183.0),
                ("scale_factor", 0.9996),
                ("false_easting", 500000.0),
                ("false_northing", family.Northern ? 0.0 : 10_000_000.0),
            ]);
    }

    private static ProjCs.GeographicCoordinateSystem BuildGeographic(GeographicEntry entry)
    {
        var ellipsoid = Factory.CreateFlattenedSphere(entry.EllipsoidName, entry.SemiMajor, entry.InverseFlattening, ProjCs.LinearUnit.Metre);
        var datum = Factory.CreateHorizontalDatum(
            entry.DatumName,
            ProjCs.DatumType.HD_Geocentric,
            ellipsoid,
            new ProjCs.Wgs84ConversionInfo(entry.ToWgs84[0], entry.ToWgs84[1], entry.ToWgs84[2], entry.ToWgs84[3], entry.ToWgs84[4], entry.ToWgs84[5], entry.ToWgs84[6]));
        return Factory.CreateGeographicCoordinateSystem(
            entry.Name, ProjCs.AngularUnit.Degrees, datum, Greenwich, LonAxis, LatAxis);
    }

    private static ProjCs.ProjectedCoordinateSystem BuildProjected(ProjectedEntry entry)
    {
        var geodetic = (ProjCs.GeographicCoordinateSystem)Entries[entry.GeodeticCode].Value;
        var parameters = entry.Parameters
            .Select(parameter => new ProjCs.ProjectionParameter(parameter.Name, parameter.Value))
            .ToList();
        var projection = Factory.CreateProjection(entry.ProjectionClass, entry.ProjectionClass, parameters);
        return Factory.CreateProjectedCoordinateSystem(
            entry.Name, geodetic, projection, ProjCs.LinearUnit.Metre, EastingAxis, NorthingAxis);
    }

    private static readonly ProjCs.CoordinateSystemFactory Factory = new();

    private static readonly ProjCs.PrimeMeridian Greenwich =
        Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0);

    private static readonly ProjCs.AxisInfo LonAxis = new("Lon", ProjCs.AxisOrientationEnum.East);
    private static readonly ProjCs.AxisInfo LatAxis = new("Lat", ProjCs.AxisOrientationEnum.North);
    private static readonly ProjCs.AxisInfo EastingAxis = new("Easting", ProjCs.AxisOrientationEnum.East);
    private static readonly ProjCs.AxisInfo NorthingAxis = new("Northing", ProjCs.AxisOrientationEnum.North);

    private static readonly Dictionary<int, Lazy<ProjCs.CoordinateSystem>> Entries = BuildEntries();
}
