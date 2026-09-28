using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Invokes the graph through the public service, so the tests read as the
/// contract the rest of the engine sees (ADR-0033: implementations are
/// reached through <c>ICrsDirectory</c>, never by their private types).
/// </summary>
internal static class GraphInvoker
{
    private static readonly ProjNetTransforms Directory = new();

    /// <summary>The ranked candidates between two CRSs, optionally filtered by area of interest.</summary>
    public static IReadOnlyList<CrsTransformation> Search(string source, string target, CrsAreaOfUse? areaOfInterest = null) =>
        Directory.FindTransformations(new CrsTransformationQuery(source, target, areaOfInterest));

    /// <summary>The search with a caller-supplied token, for the failure paths.</summary>
    public static IReadOnlyList<CrsTransformation> Search(CrsTransformationQuery query, CancellationToken cancellationToken) =>
        Directory.FindTransformations(query, cancellationToken);

    /// <summary>A geographic area of interest in degrees (x = longitude, y = latitude).</summary>
    public static CrsAreaOfUse NewArea(double xMin, double yMin, double xMax, double yMax) =>
        new("area of interest", xMin, yMin, xMax, yMax);
}
