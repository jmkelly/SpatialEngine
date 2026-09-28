using System.Diagnostics.CodeAnalysis;
using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The catalogue's <em>coordinate operation</em> half: what EPSG publishes
/// about taking each catalogued datum to WGS 84, and the area that operation
/// is registered over (ADR-0086).
/// <para>
/// These are not CRS properties. A definition says which datum a coordinate
/// system is on, what its ellipsoid is and which shift to WGS 84 the engine
/// applies — all of that is in the vendored WKT and nowhere else. How
/// <em>accurate</em> that shift is, and over <em>what ground</em> it means
/// anything, are attributes of the transformation EPSG registers against that
/// datum, and WKT does not carry them: a WKT2 <c>GEOGCRS</c> has no accuracy
/// and no usage, while the same datum inside a <c>BOUNDCRS</c> does. So the
/// two live here, in the operation table the registry actually publishes, and
/// are joined to a definition by the datum's EPSG name — the identifier the
/// vendored <c>DATUM</c> node actually carries.
/// </para>
///
/// EPSG Geodetic Parameter Dataset © IOGP/EPSG (subset reproduced here under
/// the EPSG terms of use; see ADR-0027).
/// </summary>
internal static class EpsgDatumOperations
{
    /// <summary>
    /// The published operations: each catalogued datum, the accuracy EPSG
    /// states for its grid-free Helmert path to WGS 84 in metres, and the
    /// extent that path is registered over in degrees (the unit EPSG records
    /// extents in). The accuracies are the registry's, not round numbers
    /// chosen here — notably OSGB36's, which is metres rather than
    /// centimetres because the WKT1 library has no OSTN grid (ADR-0027
    /// §accuracy).
    /// </summary>
    private static readonly DatumOperation[] Operations =
    [
        new(6326, "WGS84", "World Geodetic System 1984", 0.0, "World", [-180.0, -90.0, 180.0, 90.0]),
        new(6258, "ETRS89", "European Terrestrial Reference System 1989", 1.0, "Europe", [-16.1, 32.88, 40.18, 84.73]),
        new(6269, "NAD83", "North American Datum 1983", 2.0, "North America", [-172.54, 23.81, -47.74, 86.46]),
        new(6277, "OSGB36", "Ordnance Survey of Great Britain 1936", 3.0, "Great Britain", [-8.82, 49.79, 1.92, 60.94]),
        new(6171, "RGF93", "Reseau Geodesique Francais 1993", 1.0, "France", [-9.86, 41.15, 10.38, 51.56]),
        new(6167, "NZGD2000", "New Zealand Geodetic Datum 2000", 1.0, "New Zealand", [166.36, -46.64, 178.52, -34.1]),
    ];

    /// <summary>
    /// The node the transformation graph runs on (ADR-0087): the datum's shift
    /// to WGS 84, which the definition carries, together with the accuracy and
    /// area of use of the registered operation that shift stands for, which
    /// only this table carries. The shift is not restated here — reading it
    /// back out of the definition is the point, so a datum's parameters have
    /// exactly one home.
    /// </summary>
    public static bool TryGetNode(GeodeticDefinition definition, [NotNullWhen(true)] out DatumNode? node)
    {
        if (!TryGetOperation(definition.DatumName, out var operation))
        {
            node = null;
            return false;
        }

        node = new DatumNode(
            operation.GraphName,
            definition.DatumName,
            new HelmertParameters(
                definition.ToWgs84[0], definition.ToWgs84[1], definition.ToWgs84[2],
                definition.ToWgs84[3], definition.ToWgs84[4], definition.ToWgs84[5], definition.ToWgs84[6]),
            operation.AccuracyMetres,
            operation.AreaOfUse);
        return true;
    }

    /// <summary>The EPSG datum names this catalogue publishes an operation for.</summary>
    public static IEnumerable<string> DatumNames => Operations.Select(operation => operation.DatumName);

    /// <summary>
    /// Test pin: reads one operation row by datum name, so the join between a
    /// definition and the table it needs is exercised rather than assumed.
    /// </summary>
    public static DatumOperation? ReadForTest(string datumName) =>
        TryGetOperation(datumName, out var operation) ? operation : null;

    private static bool TryGetOperation(string datumName, [NotNullWhen(true)] out DatumOperation? operation)
    {
        foreach (var candidate in Operations)
        {
            if (string.Equals(candidate.DatumName, datumName, StringComparison.Ordinal))
            {
                operation = candidate;
                return true;
            }
        }

        operation = null;
        return false;
    }

    /// <summary>
    /// One registered datum-to-WGS 84 operation. <paramref name="DatumCode"/>
    /// is the EPSG geodetic datum the row is about;
    /// <paramref name="GraphName"/> is the short token the graph's operation
    /// names are built from, so a client reading
    /// <c>WGS84_To_OSGB36_Helmert</c> back is reading a name and not a
    /// code it has to look up.
    /// </summary>
    public sealed record DatumOperation(
        int DatumCode,
        string GraphName,
        string DatumName,
        double AccuracyMetres,
        string AreaOfUseName,
        double[] AreaOfUseBounds)
    {
        /// <summary>The registered extent, in the degrees EPSG records extents in.</summary>
        public CrsAreaOfUse AreaOfUse =>
            new(AreaOfUseName, AreaOfUseBounds[0], AreaOfUseBounds[1], AreaOfUseBounds[2], AreaOfUseBounds[3]);
    }
}
