namespace Spatial.Core.Geometry;

/// <summary>
/// The DE-9IM intersection-pattern grammar — the one place that knows what a
/// pattern is allowed to say, so the engine's relation verb and every
/// consumer of it agree on it (ADR-0036, SpatialEngine-imj).
///
/// A pattern is nine cells, one per pair of the two geometries' components
/// (interior, boundary, exterior) in interior/boundary/exterior order. Each
/// cell is <c>T</c> (non-empty, whatever its dimension), <c>F</c> (empty),
/// <c>0</c>/<c>1</c>/<c>2</c> (empty, a point, a curve, an area) or <c>*</c>
/// (don't care). Nothing else is a cell: a shorter string is not a compact
/// spelling of a pattern, and a longer one is not a pattern either.
///
/// The grammar lives here rather than in the implementation because it is
/// vocabulary rather than algorithm, and because it has to be checked in two
/// directions: the relation verb rejects a pattern it cannot answer, and the
/// protocol boundary has to tell a client's pattern apart from a client's
/// relation <em>name</em> before it hands one over. A boundary that
/// recognises only part of the alphabet rejects a legal pattern by name; a
/// verb that recognises none of it answers a malformed pattern as though the
/// odd cell were a constraint that fails — which is a parameter accepted and
/// ignored, and answers a question the caller did not ask.
/// </summary>
public static class De9imPattern
{
    /// <summary>The number of cells in a pattern: one per component pair.</summary>
    public const int Length = 9;

    /// <summary>The cells a pattern cell may hold, in the order the grammar states them.</summary>
    public const string Symbols = "TF012*";

    /// <summary>
    /// The grammar, as it is named back to a caller that got it wrong: nine
    /// cells drawn from <see cref="Symbols"/>.
    /// </summary>
    public const string Grammar =
        "a DE-9IM intersection pattern is nine cells, one per pair of the geometries' "
        + "interior, boundary and exterior components, each cell being 'T' (non-empty), "
        + "'F' (empty), '0', '1' or '2' (that dimension) or '*' (don't care)";

    /// <summary>
    /// Whether <paramref name="pattern"/> is a pattern this engine can answer:
    /// nine characters, every one of them a cell. Surrounding whitespace is
    /// not forgiven — a pattern is written out in full at the call site, and
    /// a pattern that arrived padded is a pattern this engine did not
    /// receive.
    /// </summary>
    public static bool IsPattern(string? pattern) =>
        pattern is { Length: Length } && pattern.All(symbol => Symbols.Contains(symbol, StringComparison.Ordinal));
}
