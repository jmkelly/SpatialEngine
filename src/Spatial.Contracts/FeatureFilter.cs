using Spatial.Core.Features.Query;

namespace Spatial.Contracts;

/// <summary>
/// The published filter <em>text</em> of a feature query, parsed once at the
/// boundary into the plan's predicate (ADR-0074 §3). The text a client sends
/// on <c>GET /api/features/query?filter=</c>, in a render layer's
/// <c>filter</c>, or in a MapServer <c>layerDefs</c> clause keeps its syntax —
/// one breaking change at a time — but no store parses it any more, and it can
/// never reach SQL as text: the tree it produces is compiled with every
/// literal bound as a parameter.
/// </summary>
public static class FeatureFilter
{
    /// <summary>
    /// The predicate the text describes, or null when no filter was sent. A
    /// text outside the grammar is a typed <c>invalid.arguments</c> failure
    /// naming the position, never a filter that is silently dropped.
    /// </summary>
    public static Predicate? Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        return FeatureFilterText.TryParse(filter, out var predicate, out var error)
            ? predicate
            : throw SpatialException.BadArguments($"The filter cannot be parsed: {error}");
    }
}
