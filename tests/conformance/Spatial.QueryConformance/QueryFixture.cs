using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.QueryConformance;

/// <summary>
/// The shared fixture every store's conformance test runs over: one dataset
/// whose rows are built to make a pushdown <em>tempted</em> to differ from the
/// reference. Every case below exists because a real dialect gets one of them
/// wrong:
///
/// <list type="bullet">
/// <item><b>Ties</b> — several rows share the order key, so only the identity
/// tie-break separates them and a page boundary can land between them.</item>
/// <item><b>Nulls</b> — in the order key (nulls sort last ascending), in a
/// summed field (a null is skipped, and a group of all nulls sums to null, not
/// to zero) and in a group key (a null is a group of its own).</item>
/// <item><b>Single-row groups</b> — where a percentile's rank arithmetic or a
/// variance's n − 1 divides by zero, or is taken to divide.</item>
/// <item><b>A group with two distinct values</b> — so a percentile
/// interpolates between rows rather than answering a value that happens to be
/// in the group, which is the only case where
/// <c>PERCENTILE_CONT</c> and a hand-rolled interpolation can disagree.</item>
/// <item><b>An empty set</b> — for the one-row-of-nulls rule, and for a count
/// that is zero rather than one.</item>
/// <item><b>Text sort keys whose locale-collation order is not their ordinal
/// order</b> — the group key and the label both carry case (<c>a</c> beside
/// <c>A</c>) and punctuation (<c>_c</c>, <c>_charlie</c>, <c>a-delta</c>), which
/// is what separates a byte comparison from a locale one: <c>C</c> orders
/// <c>"A"</c> before <c>"a"</c> and puts <c>"_c"</c> between the upper- and
/// lower-case letters, while <c>en_US.utf8</c> orders <c>"a"</c> before
/// <c>"A"</c> and pushes the punctuated names to where their letters sort. A
/// pushed-down <c>ORDER BY</c> that inherits the database's collation therefore
/// returns a <em>different sequence</em> from the reference for the same plan
/// (ADR-0098 §3, ADR-0121).</item>
/// </list>
///
/// </summary>
public static class QueryFixture
{
    /// <summary>The fixture's schema: a group key, a sort key with nulls, a
    /// numeric column with nulls, a label, and a geometry for the box
    /// pre-filter.</summary>
    public static FeatureSchema Schema { get; } = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("category", AttributeKind.String, nullable: true),
        new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        new FieldDefinition("ratio", AttributeKind.Double, nullable: true),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("shape", AttributeKind.Geometry),
    ]);

    /// <summary>The fixture's rows, in the order a store returns them when it
    /// is asked for nothing: two tied sort keys, four categories (two of them a
    /// single row, one with a null key and two with a null summed value), nulls
    /// in the summed field, and one row outside the box the paging cases use.
    ///
    /// <para>The two text columns are the ones a collation gets wrong. The
    /// group key is <c>a, a, A, A, null, _c</c> and the label is <c>Alpha,
    /// bravo, _charlie, Charlie, a-delta, echo</c>: byte order puts
    /// <c>"A"</c> and <c>"_c"</c> before the lower-case names, and
    /// <c>en_US.utf8</c> puts them after, so no two of the six rows are in the
    /// same place under both rules. The <em>group</em> shape is unchanged by
    /// that — still two groups of two rows, a null-keyed group of its own and a
    /// single-row group — so a percentile that interpolates and a variance that
    /// divides are still measured.</para>
    /// </summary>
    public static IReadOnlyList<Feature> Features { get; } =
    [
        Row(1, "a", 10, 1.5, "Alpha", x: 1.0),
        Row(2, "a", 15, 2.5, "bravo", x: 1.0),
        Row(3, "A", null, 0.5, "_charlie", x: 2.0),
        Row(4, "A", 30, null, "Charlie", x: 2.0),
        Row(5, null, 20, 4.5, "a-delta", x: 3.0),
        Row(6, "_c", 20, 4.5, "echo", x: 9.0),
    ];

    /// <summary>The box the paged cases use: it selects four of the six rows.</summary>
    public static BoundingBox Box { get; } = new(0.5, 0.5, 4.5, 4.5);

    /// <summary>A box that selects nothing, for the empty-set rules.</summary>
    public static BoundingBox EmptyBox { get; } = new(-50, -50, -40, -40);

    private static Feature Row(long id, string? category, long? score, double? ratio, string name, double x) =>
        new(
            new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Schema,
            [
                AttributeValue.FromInt64(id),
                category is null ? AttributeValue.Null : AttributeValue.FromString(category),
                score is null ? AttributeValue.Null : AttributeValue.FromInt64(score.Value),
                ratio is null ? AttributeValue.Null : AttributeValue.FromDouble(ratio.Value),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, x)),
            ]);
}
