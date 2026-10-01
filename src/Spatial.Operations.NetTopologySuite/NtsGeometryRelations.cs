using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite spatial-relation service (ADR-0036): an in-process
/// implementation of <see cref="IGeometryRelations"/> over the OGC DE-9IM
/// relate operation. NTS types stay inside this assembly (ADR-0005).
///
/// The pattern is checked against <see cref="De9imPattern"/> before the
/// provider sees it, and that is the whole of the fix for the divergence
/// SpatialEngine-imj was opened for. A pattern is answered exactly as the
/// intersection matrix reads, cell by cell — including the empty-or-point
/// cells, which match <c>T</c> and <c>0</c> in every position like any
/// other — so there is no wildcard reading to reconcile with the exact one.
/// What the provider cannot be trusted with is the grammar: NTS 2.6 rejects
/// a pattern that is not nine characters with an <c>ArgumentException</c>
/// whose message quotes a length, and it reads a cell it does not recognise
/// (<c>X</c>, <c>E</c>, a space) as a constraint that simply fails, so
/// <c>T*T***T*X</c> answers false where the caller plainly meant
/// <c>T*T***T**</c>. Both are the same defect seen from two sides — a
/// malformed pattern answered instead of rejected — and the grammar is
/// checked once, here, in the one implementation every consumer reaches.
///
/// The cells themselves are the provider's, and ADR-0166 records what they
/// are rather than recomputing them: <c>Relate(g)</c> and
/// <c>Relate(g, pattern)</c> are one computation in NetTopologySuite 2.6, so
/// a pattern and the rendered matrix cannot disagree; a 0-D operand
/// contributes no boundary, so a point on a line's endpoint meets it in the
/// line's <em>boundary</em> row and the pair is not disjoint; and a
/// vertex-only contact is dimension 0 in position 5, one cell from the
/// dimension 1 of an edge-sharing one. None of those is wrapped here, and the
/// one place the served pattern table and the provider's own named predicates
/// do disagree — <c>Crosses</c> for a line lying wholly inside an area and
/// touching its boundary — is named, measured and pinned in
/// <c>NtsRelateCellSemanticsTests</c> rather than papered over.
/// </summary>
public sealed class NtsGeometryRelations : IGeometryRelations
{
    public bool Relate(IGeometry left, IGeometry right, string intersectionPattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!De9imPattern.IsPattern(intersectionPattern))
        {
            throw SpatialException.BadArguments(
                $"'intersectionPattern' is not a DE-9IM pattern: {Describe(intersectionPattern)}. {De9imPattern.Grammar}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToNts(left).Relate(GeometryAdapter.ToNts(right), intersectionPattern);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "relation");
        }
    }

    /// <summary>
    /// What the caller actually sent, named without echoing an unbounded
    /// client string back through an error message: the length, and the
    /// cells that are not cells.
    /// </summary>
    private static string Describe(string? pattern) =>
        pattern is null
            ? "it was null"
            : pattern.Length != De9imPattern.Length
                ? $"it is {pattern.Length} characters long"
                : $"'{pattern}' holds a cell that is not one of {De9imPattern.Symbols}";
}
