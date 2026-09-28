using Spatial.Core.Features;

namespace Spatial.Core.Features.Query;

/// <summary>
/// Which comparisons the one predicate vocabulary (ADR-0074 §2) can answer at
/// all, as a property of the value types rather than of any back end
/// (ADR-0075 §2).
/// <para>
/// A <see cref="Literal"/> carries its own <see cref="LiteralKind"/> and a
/// column carries an <see cref="AttributeKind"/>, and not every pair is a
/// comparison with a meaning. The reference evaluator treats an
/// unanswerable pair as "matches nothing" rather than coercing, and this table
/// is that same classification, stated once: a store that compiles a plan to
/// SQL has to decide the same question, because emitting the comparison
/// anyway asks the server about a pair of types it has no operator for
/// (<c>uuid = text</c> in PostGIS, <c>text &gt; int</c> in SQL Server), which
/// fails at execution as a store error for what is really a filter that
/// matched no rows.
/// </para>
/// <para>
/// This is structural — it inspects kinds and reads no feature, binds no
/// parameter and opens no connection — so it lives beside the tree it
/// describes. The <em>evaluation</em> of a plan stays out of Core: that is the
/// reference evaluator in the in-memory store and the residual evaluators
/// behind the adapters.
/// </para>
/// </summary>
public static class PredicateCompatibility
{
    /// <summary>
    /// Whether a column of <paramref name="kind"/> can ever satisfy
    /// <paramref name="comparison"/> against <paramref name="literal"/>.
    /// False means the comparison matches nothing, never that the plan is
    /// malformed: every back end answers the same rows either way, which is
    /// what makes pushdown result-preserving (ADR-0074 §4).
    /// </summary>
    public static bool CanMatch(ComparisonOperator comparison, Literal literal, AttributeKind kind)
    {
        if (kind == AttributeKind.Geometry)
        {
            // Geometry is not an attribute comparison at all: a geometry
            // column is addressed by the bounding box, and a provider
            // rejects it by name rather than answering it.
            return false;
        }

        if (kind == AttributeKind.Guid)
        {
            return Equality(comparison)
                && literal.Kind == LiteralKind.String
                && Guid.TryParse(literal.Text, out _);
        }

        if (kind == AttributeKind.Boolean)
        {
            // A flag has two values, so the engine promises only whether it is
            // equal; `<` and `LIKE` would ask the server about an ordering the
            // reference evaluator never gives.
            return Equality(comparison) && literal.Kind == LiteralKind.Boolean;
        }

        if (comparison == ComparisonOperator.Like)
        {
            return kind == AttributeKind.String && literal.Kind == LiteralKind.String;
        }

        return kind switch
        {
            AttributeKind.String => literal.Kind == LiteralKind.String,
            // A date-time is an instant, so it answers a date-time literal and
            // any number, the same way the reference evaluator reads both as
            // the same epoch axis.
            AttributeKind.DateTimeOffset or AttributeKind.Int64 or AttributeKind.Double =>
                literal.Kind is LiteralKind.Integer or LiteralKind.Decimal or LiteralKind.DateTime,
            _ => false,
        };
    }

    /// <summary>Whether the operator is one the vocabulary answers for a total-ordered or unordered value alike.</summary>
    private static bool Equality(ComparisonOperator comparison) =>
        comparison is ComparisonOperator.Equals or ComparisonOperator.NotEquals;
}
