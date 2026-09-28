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
/// <item><b>An empty set</b> — for the one-row-of-nulls rule, and for a count
/// that is zero rather than one.</item>
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
    /// is asked for nothing: two tied sort keys, three categories (one with a
    /// single row, one with a null key), nulls in the summed field, and one
    /// row outside the box the paging cases use.</summary>
    public static IReadOnlyList<Feature> Features { get; } =
    [
        Row(1, "a", 10, 1.5, "alpha", x: 1.0),
        Row(2, "a", 10, 2.5, "bravo", x: 1.0),
        Row(3, "b", null, 0.5, "charlie", x: 2.0),
        Row(4, "b", 30, null, "delta", x: 2.0),
        Row(5, null, 20, 4.5, "echo", x: 3.0),
        Row(6, "c", 20, 4.5, "foxtrot", x: 9.0),
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
