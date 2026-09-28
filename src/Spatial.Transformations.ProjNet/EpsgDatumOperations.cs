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
    /// <para>
    /// Every row is a transcription of one record of the <em>EPSG Geodetic
    /// Parameter Dataset v13.102</em>, and each row says which: the
    /// <paramref name="OperationCode"/> is the
    /// <c>helmert_transformation</c> the accuracy was read from, the
    /// <paramref name="ExtentCode"/> is the <c>extent</c> the bounds were read
    /// from, and the vendored definition for every one of these datums is the
    /// operation's own scope CRS — so a row is checked by opening one EPSG
    /// record, and a row that has drifted names the record it drifted from.
    /// <c>EpsgDatumOperationsTests</c> pins all of it.
    /// </para>
    /// <para>
    /// <paramref name="AreaOfUseName"/> is the one value that is not the
    /// registry's verbatim: EPSG's extent names run to sentences
    /// ("North America - Canada and USA (CONUS, Alaska mainland)"), and the
    /// name is composed into client-facing strings, so each row carries a
    /// short label for the extent it cites. The bounds are the extent's own.
    /// </para>
    /// </summary>
    private static readonly DatumOperation[] Operations =
    [
        // EPSG:6326 is the WGS 84 datum ensemble, and the graph's pivot rather
        // than a published operation - a datum already at the pivot has no
        // shift to state an accuracy for, so the 0.0 is the absence of an
        // operation, not a measured figure. Extent 1262 "World".
        new(6326, "WGS84", "World Geodetic System 1984", 0.0, "World", [-180.0, -90.0, 180.0, 90.0], 0, 1262),
        // EPSG:1149 "ETRS89 to WGS 84 (1)", scope CRS EPSG:4258 - the vendored
        // ETRS89 definition. Extent 4755 "Europe - ETRF by country":
        // 16.1W-38.0E, 33.26N-84.73N.
        new(6258, "ETRS89", "European Terrestrial Reference System 1989", 1.0, "Europe", [-16.1, 33.26, 38.0, 84.73], 1149, 4755),
        // EPSG:1188 "NAD83 to WGS 84 (1)", scope CRS EPSG:4269 - the vendored
        // NAD83 definition. The published accuracy is 4.0 m; the "Accuracy 2m
        // in each axis" in the record's own remarks is a note on how the
        // parameters were derived, not the accuracy of the operation, and is
        // not what a row about a published operation may carry. Extent 1325
        // "North America - Canada and USA (CONUS, Alaska mainland)".
        new(6269, "NAD83", "North American Datum 1983", 4.0, "North America", [-172.54, 23.81, -47.74, 86.46], 1188, 1325),
        // EPSG:1314 "OSGB36 to WGS 84 (6)", scope CRS EPSG:4277 - the vendored
        // OSGB36 definition, and the operation whose seven parameters are
        // exactly the definition's TOWGS84 node, so it is the one this row
        // describes. Metre-level because the engine applies this Helmert rather
        // than the OSTN grid shift. Extent 1264 "UK - Great Britain onshore and
        // nearshore; Isle of Man".
        new(6277, "OSGB36", "Ordnance Survey of Great Britain 1936", 2.0, "Great Britain", [-8.82, 49.79, 1.92, 60.94], 1314, 1264),
        // EPSG:1671 "ETRS89-FRA [RGF93 v1] to WGS 84 (1)", scope CRS
        // EPSG:4171 - the vendored RGF93 v1 definition. Extent 1096 "France".
        new(6171, "RGF93", "Reseau Geodesique Francais 1993", 1.0, "France", [-9.86, 41.15, 10.38, 51.56], 1671, 1096),
        // EPSG:1565 "NZGD2000 to WGS 84 (1)", scope CRS EPSG:4167 - the
        // geographic base of the vendored EPSG:2193. Extent 1175 "New Zealand"
        // registers 55.95S-25.88S and 160.6E-171.2W, so the longitude span
        // crosses the antimeridian; the latitudes and the western bound are the
        // registered ones, and the eastern bound is the registered extent
        // clipped at the edge of the world, because an area of use here is a
        // box and a wrapped extent read as one reads as empty and would drop
        // New Zealand out of the graph. The cost is that ground west of the
        // antimeridian inside the registered extent - the Chathams - is
        // outside the clipped box; that gap is a wrapped area of use, which no
        // ADR has authorised.
        new(6167, "NZGD2000", "New Zealand Geodetic Datum 2000", 1.0, "New Zealand", [160.6, -55.95, 180.0, -25.88], 1565, 1175),
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
    /// code it has to look up;
    /// <paramref name="OperationCode"/> and <paramref name="ExtentCode"/> are
    /// the EPSG records the accuracy and the bounds were read from, so a
    /// reader can check the row against the registry and a row that drifts
    /// names where it drifted from. Both are 0 only for the WGS 84 pivot,
    /// which is not a published operation.
    /// </summary>
    public sealed record DatumOperation(
        int DatumCode,
        string GraphName,
        string DatumName,
        double AccuracyMetres,
        string AreaOfUseName,
        double[] AreaOfUseBounds,
        int OperationCode,
        int ExtentCode)
    {
        /// <summary>The registered extent, in the degrees EPSG records extents in.</summary>
        public CrsAreaOfUse AreaOfUse =>
            new(AreaOfUseName, AreaOfUseBounds[0], AreaOfUseBounds[1], AreaOfUseBounds[2], AreaOfUseBounds[3]);
    }
}
