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

    /// <summary>
    /// The bundle that serves a datum, by the datum's short graph token, or
    /// null when the catalogue publishes no grid for it — in which case the
    /// Helmert path stands and says so.
    /// <para>
    /// The join is on the token <see cref="EpsgDatumOperations"/> publishes as
    /// each operation's <c>GraphName</c>, because that is what a graph node
    /// carries as its code. Keying on it means a grid row is joined the same
    /// way a Helmert row is, and neither table needs a second copy of the
    /// catalogue's datum codes to be cross-checked against.
    /// </para>
    /// </summary>
    public static GridShiftOperation? For(string graphName) =>
        Operations.FirstOrDefault(operation =>
            string.Equals(operation.GraphName, graphName, StringComparison.Ordinal));

    /// <summary>The datum tokens a grid operation is published for.</summary>
    public static IEnumerable<string> GraphNames => Operations.Select(operation => operation.GraphName);

    /// <summary>Test pin: the file name the catalogue names for a datum's grid.</summary>
    public static string? FileNameForTest(string graphName) => For(graphName)?.FileName;

    /// <summary>
    /// One published grid-backed datum operation: the datum the shift starts
    /// from (by its graph token), the file in a configured directory that
    /// carries it, the datum it reaches, and the region it is registered over.
    /// The area is a cross-check on the grid's own block, not a substitute for
    /// it: what the operation is actually valid over is read off the file.
    /// </summary>
    public sealed record GridShiftOperation(
        string GraphName,
        string FileName,
        int TargetDatumCode,
        string AreaOfUseName);
}
