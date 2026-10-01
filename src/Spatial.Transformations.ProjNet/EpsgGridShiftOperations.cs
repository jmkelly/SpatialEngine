namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The catalogue's <em>grid</em> operation rows (ADR-0105): which published
/// datum shift is served by a grid, and which file in a configured grid
/// directory carries it.
/// <para>
/// These sit beside <see cref="EpsgDatumOperations"/> rather than inside it,
/// because they answer a different question. That table is the registry's
/// grid-free Helmert path to WGS 84 and what EPSG says about <em>it</em> — an
/// accuracy in metres and a registered area of use. A grid operation is a
/// different operation, and under ADR-0086 a different kind of row, not a new
/// column on the Helmert one. It also deliberately carries no accuracy and no
/// extent: both are derived from the grid file itself, because a grid is only
/// as good as its worst node and only applies over its own block, and a number
/// typed out beside the file name would be a second claim about the same
/// operation that could disagree with it.
/// </para>
/// <para>
/// The accuracy a grid-backed transformation is published with is therefore
/// not restated here either. It is combined at publication time from the
/// grid's own worst-node accuracy and the other leg's registered accuracy, the
/// same way ADR-0087 combines two Helmert accuracies in quadrature.
/// </para>
/// </summary>
internal static class EpsgGridShiftOperations
{
    /// <summary>The published bundle carrying the Ordnance Survey datum shift to WGS 84.</summary>
    public const string OsgbBundle = "OSTN15_osgb_02_NTv2_OSGBtoETRS.gsb";

    /// <summary>The EPSG code of the datum every catalogued grid reaches: the catalogue's WGS 84 pivot.</summary>
    private const int WorldDatumCode = 4326;

    private static readonly GridShiftOperation[] Operations =
    [
        new("OSGB36", OsgbBundle, WorldDatumCode, "Great Britain"),
        new("NAD83", "NAD83_to_WGS84_NTv2.gsb", WorldDatumCode, "North America"),
    ];

    /// <summary>Every published grid operation, across every datum the registry serves.</summary>
    public static IReadOnlyList<GridShiftOperation> Bundles => Operations;

    /// <summary>
    /// Every bundle a datum may be served by, in the order the catalogue
    /// prefers them: the first directory holding any of them wins, and within
    /// one directory the rows are tried in this order.
    /// <para>
    /// The order is a list rather than a set because a datum may be served by
    /// more than one bundle — an NTv2 file and a NADCON pair can both answer
    /// for a North American datum — and an operator is entitled to deploy
    /// either.
    /// </para>
    /// <para>
    /// The join is on the token <see cref="EpsgDatumOperations"/> publishes as
    /// each operation's <c>GraphName</c>, because that is what a graph node
    /// carries as its code. Keying on it means a grid row is joined the same
    /// way a Helmert row is, and neither table needs a second copy of the
    /// catalogue's datum codes to be cross-checked against. A datum with no
    /// row is served no grid, and the Helmert path stands and says so.
    /// </para>
    /// </summary>
    public static IReadOnlyList<GridShiftOperation> For(string graphName) =>
        [.. Bundles.Where(operation => string.Equals(operation.GraphName, graphName, StringComparison.Ordinal))];

    /// <summary>The datum tokens a grid operation is published for.</summary>
    public static IEnumerable<string> GraphNames => Operations.Select(operation => operation.GraphName);

    /// <summary>Test pin: the file name the catalogue names for a datum's grid.</summary>
    public static string? FileNameForTest(string graphName)
    {
        var bundles = For(graphName);
        return bundles.Count > 0 ? bundles[0].FileName : null;
    }

    /// <summary>
    /// One published grid-backed datum operation: the datum the shift starts
    /// from (by its graph token), the file in a configured directory that
    /// carries it, the datum it reaches, and the region it is registered over.
    /// The area is a cross-check on the grid's own block, not a substitute for
    /// it: what the operation is actually valid over is read off the file.
    /// <para>
    /// <paramref name="LongitudeFileName"/> is the <c>.los</c> half of a NADCON
    /// pair, whose <paramref name="FileName"/> is the <c>.las</c> half. NADCON
    /// splits the latitude shifts and the longitude shifts into two files of
    /// the same shape, and one file name cannot express that; it is null for a
    /// bundle that is a single file.
    /// </para>
    /// <para>
    /// <paramref name="AccuracyMetres"/> is null wherever the file states its
    /// own accuracy, which is the normal case and the reason ADR-0105 §9 keeps
    /// the figure out of this table. A NADCON shift record holds the shift
    /// alone, so the grid one produces has no worst node to read, and the row
    /// carries the published figure instead (ADR-0168).
    /// </para>
    /// </summary>
    public sealed record GridShiftOperation(
        string GraphName,
        string FileName,
        int TargetDatumCode,
        string AreaOfUseName,
        string? LongitudeFileName = null,
        double? AccuracyMetres = null);
}
