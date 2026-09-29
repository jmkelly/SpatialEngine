using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Querying;

/// <summary>
/// The value ordering a plan's sort keys are compared under, and the row
/// equality a deduplication is decided by. These two rules are the ones a
/// pushed-down <c>ORDER BY</c> or <c>GROUP BY</c> must reproduce exactly, so
/// they live here once and every store's pushdown is measured against them
/// (ADR-0074 §4).
///
/// <para>
/// The rules, stated so a dialect can be checked against them: nulls sort
/// <em>last</em> in ascending order (and therefore first when the key is
/// reversed for <c>DESC</c>); strings compare ordinally, never by a locale
/// collation; a value of one kind is compared with a value of the same kind,
/// and the kinds have a fixed order when a caller compares across them;
/// date-times compare by UTC instant.
/// </para>
/// </summary>
public sealed class AttributeValueComparer : IComparer<AttributeValue>
{
    /// <summary>The single instance.</summary>
    public static readonly AttributeValueComparer Instance = new();

    /// <inheritdoc />
    public int Compare(AttributeValue left, AttributeValue right)
    {
        if (left.IsNull || right.IsNull)
        {
            return CompareNulls(left, right);
        }

        return left.Kind == right.Kind ? CompareSameKind(left, right) : left.Kind.CompareTo(right.Kind);
    }

    /// <summary>Nulls last, which a reversed key makes nulls first.</summary>
    private static int CompareNulls(AttributeValue left, AttributeValue right) =>
        left.IsNull ? (right.IsNull ? 0 : 1) : -1;

    private static int CompareSameKind(AttributeValue left, AttributeValue right) => left.Kind switch
    {
        AttributeKind.Boolean => left.BooleanValue.CompareTo(right.BooleanValue),
        AttributeKind.Int64 => left.Int64Value.CompareTo(right.Int64Value),
        AttributeKind.Double => left.DoubleValue.CompareTo(right.DoubleValue),
        AttributeKind.String => string.CompareOrdinal(left.StringValue, right.StringValue),
        AttributeKind.DateTimeOffset => left.DateTimeOffsetValue.UtcTicks.CompareTo(right.DateTimeOffsetValue.UtcTicks),
        AttributeKind.Guid => left.GuidValue.CompareTo(right.GuidValue),
        AttributeKind.Envelope => Compare(left.EnvelopeValue, right.EnvelopeValue),
        _ => 0,
    };

    /// <summary>
    /// Two rectangles compared lower-left corner first, then upper-right: a
    /// total order over an unbounded continuum of values, which an envelope
    /// never actually needs (a reduction's result is never a group key or a
    /// sort term) but which keeps the comparer honest for every kind it
    /// accepts.
    /// </summary>
    private static int Compare(Envelope left, Envelope right)
    {
        var corners = left.MinX.CompareTo(right.MinX);
        if (corners != 0)
        {
            return corners;
        }

        corners = left.MinY.CompareTo(right.MinY);
        if (corners != 0)
        {
            return corners;
        }

        corners = left.MaxX.CompareTo(right.MaxX);
        return corners != 0 ? corners : left.MaxY.CompareTo(right.MaxY);
    }
}

/// <summary>
/// Structural equality for a row of attribute values — the deduplication a
/// distinct query is decided by, and the group key a grouped reduction is
/// bucketed by.
/// </summary>
public sealed class AttributeRowComparer : IEqualityComparer<IReadOnlyList<AttributeValue>>
{
    /// <summary>The single instance.</summary>
    public static readonly AttributeRowComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(IReadOnlyList<AttributeValue>? left, IReadOnlyList<AttributeValue>? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null && left.Count == right.Count && left.SequenceEqual(right));

    /// <inheritdoc />
    public int GetHashCode(IReadOnlyList<AttributeValue> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var hash = new HashCode();
        foreach (var value in row)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
