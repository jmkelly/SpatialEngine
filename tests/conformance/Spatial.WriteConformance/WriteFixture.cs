using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.WriteConformance;

/// <summary>
/// The shared fixture every writable store's conformance test seeds: two
/// small datasets whose facts are chosen to make a write path <em>tempted</em>
/// to lose something. Every case below exists because a store gets one of
/// them wrong:
///
/// <list type="bullet">
/// <item><b>An auto-identity dataset</b> — the source supplies no key, so the
/// store must assign one, and the feature a caller reads back must carry the
/// assigned value rather than the decode's provisional number (ADR-0043,
/// ADR-0149). Nothing in the source schema names the assigned column, so its
/// position is the store's own.</item>
/// <item><b>A source-identity dataset</b> — the key is a column the source
/// carries, so a read-by-identity must resolve the column's value and an add
/// must store the value it was given (ADR-0038, ADR-0112).</item>
/// <item><b>A null attribute</b> — an update that rewrites a nullable column
/// to null must clear it rather than keep the old value, and an add must carry
/// it through.</item>
/// <item><b>A non-null attribute beside the null</b> — so a write that
/// dropped or misaligned a parameter is a wrong value, not a missing
/// one.</item>
/// </list>
/// </summary>
public static class WriteFixture
{
    /// <summary>The auto-identity schema: the store adds the key, and the
    /// source names neither an identity column nor a primary key.</summary>
    public static FeatureSchema AutoSchema { get; } = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("score", AttributeKind.Int64, nullable: true),
        new FieldDefinition("shape", AttributeKind.Geometry),
    ]);

    /// <summary>The source-identity schema: the integer <c>code</c> column is
    /// the key the ingest is told to use.</summary>
    public static FeatureSchema SourceSchema { get; } = new(
    [
        new FieldDefinition("code", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("shape", AttributeKind.Geometry),
    ]);

    /// <summary>The auto-identity rows: a null score in the second, so a write
    /// that skips a null is measurable.</summary>
    public static IReadOnlyList<Feature> AutoFeatures { get; } =
    [
        AutoRow(new FeatureId("decode-1"), "Alpha", 10, x: 1.0),
        AutoRow(new FeatureId("decode-2"), "Bravo", null, x: 2.0),
    ];

    /// <summary>The source-identity rows, keyed by the column's own values.</summary>
    public static IReadOnlyList<Feature> SourceFeatures { get; } =
    [
        SourceRow("decode-1", 10, "Alpha", x: 1.0),
        SourceRow("decode-2", 13, "Delta", x: 3.0),
    ];

    /// <summary>The auto-identity pages, split so an ingest sees more than one page.</summary>
    public static IReadOnlyList<FeatureBatch> AutoPages { get; } =
    [
        new(AutoSchema, [AutoFeatures[0]]),
        new(AutoSchema, [AutoFeatures[1]]),
    ];

    /// <summary>The source-identity pages.</summary>
    public static IReadOnlyList<FeatureBatch> SourcePages { get; } =
    [
        new(SourceSchema, [SourceFeatures[0]]),
        new(SourceSchema, [SourceFeatures[1]]),
    ];

    /// <summary>The identity column an <see cref="IngestIdentity.Auto"/> ingest assigns, in the store's own vocabulary.</summary>
    public const string AutoIdentityColumn = "id";

    /// <summary>One auto-identity row under the identity the caller wants it stored as.</summary>
    public static Feature AutoRow(FeatureId id, string name, long? score, double x) =>
        new(
            id,
            AutoSchema,
            [
                AttributeValue.FromString(name),
                score is null ? AttributeValue.Null : AttributeValue.FromInt64(score.Value),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, x, CoordinateReference.Epsg(4326))),
            ]);

    /// <summary>One source-identity row under a decode's provisional id, for a write the suite builds itself.</summary>
    public static Feature SourceRow(string decodedId, long code, string name, double x) =>
        new(
            new FeatureId(decodedId),
            SourceSchema,
            [
                AttributeValue.FromInt64(code),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, x, CoordinateReference.Epsg(4326))),
            ]);
}
