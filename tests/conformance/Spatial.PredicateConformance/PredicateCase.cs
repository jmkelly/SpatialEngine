namespace Spatial.PredicateConformance;

/// <summary>
/// One query every store must answer identically (ADR-0074 §4: pushdown is
/// optional, semantics-preserving). A case is the published filter
/// <em>text</em> plus the optional bounding box, and the codes of the rows it
/// must select — so the in-memory evaluator, the PostGIS pushdown and the
/// SQL Server pushdown are held to one answer, not three plausible ones.
/// </summary>
/// <param name="Name">What the case pins, for the failure message.</param>
/// <param name="Filter">The filter text, parsed by the shared boundary grammar.</param>
/// <param name="ExpectedCodes">The row codes the plan must select, in no order.</param>
/// <param name="BoundingBox">The bounding-box pre-filter, when the case is about combining the two.</param>
public sealed record PredicateCase(
    string Name,
    string Filter,
    IReadOnlyList<string> ExpectedCodes,
    BoundingBox? BoundingBox = null);

/// <summary>
/// The bounding box of a conformance case, x-first, in the dataset's own CRS.
/// Declared here rather than taken from the contract so the fixture stays one
/// value vocabulary wide.
/// </summary>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
