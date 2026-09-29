using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;
using Xunit;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Invokes the graph through the public service, so the tests read as the
/// contract the rest of the engine sees (ADR-0033: implementations are
/// reached through <c>ICrsDirectory</c>, never by their private types).
/// </summary>
internal static class GraphInvoker
{
    /// <summary>The ranked candidates between two CRSs, optionally filtered by area of interest.</summary>
    public static IReadOnlyList<CrsTransformation> Search(string source, string target, CrsAreaOfUse? areaOfInterest = null) =>
        Search(source, target, DatumShiftGridRegistry.Empty, areaOfInterest);

    /// <summary>
    /// The ranked candidates with the grid registry the host was configured
    /// with, so a test can publish the same search with and without a
    /// deployed bundle.
    /// </summary>
    public static IReadOnlyList<CrsTransformation> Search(
        string source,
        string target,
        DatumShiftGridRegistry datumShiftGridRegistry,
        CrsAreaOfUse? areaOfInterest = null) =>
        new ProjNetTransforms(datumShiftGridRegistry)
            .FindTransformations(new CrsTransformationQuery(source, target, areaOfInterest));

    /// <summary>The search with a caller-supplied token, for the failure paths.</summary>
    public static IReadOnlyList<CrsTransformation> Search(CrsTransformationQuery query, CancellationToken cancellationToken) =>
        Search(query, DatumShiftGridRegistry.Empty, cancellationToken);

    /// <summary>The search with a caller-supplied token and a configured grid registry.</summary>
    public static IReadOnlyList<CrsTransformation> Search(
        CrsTransformationQuery query,
        DatumShiftGridRegistry datumShiftGridRegistry,
        CancellationToken cancellationToken) =>
        new ProjNetTransforms(datumShiftGridRegistry).FindTransformations(query, cancellationToken);

    /// <summary>A geographic area of interest in degrees (x = longitude, y = latitude).</summary>
    public static CrsAreaOfUse NewArea(double xMin, double yMin, double xMax, double yMax) =>
        CrsAreaOfUse.One("area of interest", xMin, yMin, xMax, yMax);

    /// <summary>The one rectangle of an area of use, for an assertion about
    /// the bounds of a candidate that is not a wrapped one (ADR-0111).</summary>
    public static CrsAreaOfUseBox Only(CrsAreaOfUse areaOfUse)
    {
        var box = Assert.Single(areaOfUse.Boxes);
        return box;
    }
}
