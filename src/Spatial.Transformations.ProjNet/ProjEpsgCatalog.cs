using System.Diagnostics.CodeAnalysis;
using Spatial.Contracts.Transformations;
using Spatial.Contracts.TransformationSearch;
using ProjCs = ProjNet.CoordinateSystems;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The curated EPSG catalogue of the transformation provider (ADR-0027):
/// a dozen common geographic and projected CRSs, built programmatically
/// through ProjNet's factory. The catalogue is deliberately programmatic
/// rather than WKT-parsed: ProjNet 2.1's WKT reader maps the
/// "Popular Visualisation Pseudo-Mercator" projection class to a plain
/// Mercator_1SP (a ~33 km northing distortion on Web Mercator), so parsing
/// EPSG WKT at runtime is not a reliable construction path. Each
/// definition carries its datum-to-WGS84 shift ("TOWGS84"): zero for modern
/// datums, the classic Helmert approximation for OSGB36 (the WKT1 library
/// has no grid support — ADR-0027 §accuracy).
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

    /// <summary>The EPSG codes served by this catalogue.</summary>
    public static IEnumerable<int> Codes => Entries.Keys;

    /// <summary>
    /// The datum a CRS is on, for the datum-transformation graph
    /// (ADR-0074). A projected CRS resolves through its geographic parent,
    /// so a search is keyed on datums rather than on CRSs.
    /// </summary>
    public static bool TryGetDatum(int code, [NotNullWhen(true)] out DatumNode? datum)
    {
        foreach (var entry in Geographic)
        {
            if (entry.Code == code)
            {
                datum = NodeOf(entry);
                return true;
            }
        }

        foreach (var entry in Projected)
        {
            if (entry.Code == code)
            {
                var geographic = Geographic.First(candidate => candidate.Code == entry.GeodeticCode);
                datum = NodeOf(geographic);
                return true;
            }
        }

        datum = null;
        return false;
    }

    private static DatumNode NodeOf(GeographicEntry entry) => new(
        entry.Code switch
        {
            4326 => "WGS84",
            4258 => "ETRS89",
            4269 => "NAD83",
            4277 => "OSGB36",
            _ => "RGF93",
        },
        entry.DatumName,
        new HelmertParameters(entry.ToWgs84[0], entry.ToWgs84[1], entry.ToWgs84[2], entry.ToWgs84[3], entry.ToWgs84[4], entry.ToWgs84[5], entry.ToWgs84[6]),
        entry.AccuracyToWgs84Metres,
        new CrsAreaOfUse(
            entry.AreaOfUseName,
            entry.AreaOfUseBounds[0],
            entry.AreaOfUseBounds[1],
            entry.AreaOfUseBounds[2],
            entry.AreaOfUseBounds[3]));

    private sealed record GeographicEntry(
        int Code,
        string Name,
        string DatumName,
        string EllipsoidName,
        double SemiMajor,
        double InverseFlattening,
        double[] ToWgs84,
        double AccuracyToWgs84Metres,
        string AreaOfUseName,
        double[] AreaOfUseBounds);

    private sealed record ProjectedEntry(
        int Code,
        string Name,
        int GeodeticCode,
        string ProjectionClass,
        (string Name, double Value)[] Parameters);

    /// <summary>
    /// The geographic definitions, each with the datum's shift to WGS 84, the
    /// accuracy of that shift in metres and the datum's area of use in
    /// degrees (ADR-0074). The accuracies and extents are the EPSG values for
    /// the catalogue's Helmert path — notably OSGB36's, which is metres rather
    /// than centimetres because the WKT1 library has no OSTN grid (ADR-0027
    /// §accuracy).
    /// </summary>
    private static readonly GeographicEntry[] Geographic =
    [
        new(4326, "WGS 84", "World Geodetic System 1984", "WGS 84", 6378137.0, 298.257223563, [0, 0, 0, 0, 0, 0, 0], 0.0, "World", [-180.0, -90.0, 180.0, 90.0]),
        new(4258, "ETRS89", "European Terrestrial Reference System 1989", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0], 1.0, "Europe", [-16.1, 32.88, 40.18, 84.73]),
        new(4269, "NAD83", "North American Datum 1983", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0], 2.0, "North America", [-172.54, 23.81, -47.74, 86.46]),
        new(4277, "OSGB36", "Ordnance Survey of Great Britain 1936", "Airy 1830", 6377563.396, 299.3249646, [446.448, -125.157, 542.060, 0.15, 0.247, 0.842, -20.489], 3.0, "Great Britain", [-8.82, 49.79, 1.92, 60.94]),
        new(4171, "RGF93 v1", "Reseau Geodesique Francais 1993", "GRS 1980", 6378137.0, 298.257222101, [0, 0, 0, 0, 0, 0, 0], 1.0, "France", [-9.86, 41.15, 10.38, 51.56]),
    ];

    private static readonly ProjectedEntry[] Projected =
    [
        new(3857, "WGS 84 / Pseudo-Mercator", 4326, "Popular Visualisation Pseudo-Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 0.0), ("false_easting", 0.0), ("false_northing", 0.0)]),
        new(32610, "WGS 84 / UTM zone 10N", 4326, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", -123.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(32612, "WGS 84 / UTM zone 12N", 4326, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", -111.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(32632, "WGS 84 / UTM zone 32N", 4326, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 9.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(32633, "WGS 84 / UTM zone 33N", 4326, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 15.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(25832, "ETRS89 / UTM zone 32N", 4258, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 9.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(25833, "ETRS89 / UTM zone 33N", 4258, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", 15.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(26910, "NAD83 / UTM zone 10N", 4269, "Transverse_Mercator", [("latitude_of_origin", 0.0), ("central_meridian", -123.0), ("scale_factor", 0.9996), ("false_easting", 500000.0), ("false_northing", 0.0)]),
        new(27700, "OSGB36 / British National Grid", 4277, "Transverse_Mercator", [("latitude_of_origin", 49.0), ("central_meridian", -2.0), ("scale_factor", 0.9996012717), ("false_easting", 400000.0), ("false_northing", -100000.0)]),
        new(2154, "RGF93 v1 / Lambert-93", 4171, "Lambert_Conformal_Conic_2SP", [("latitude_of_origin", 46.5), ("central_meridian", 3.0), ("standard_parallel_1", 49.0), ("standard_parallel_2", 44.0), ("false_easting", 700000.0), ("false_northing", 6600000.0)]),
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

        return entries;
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
