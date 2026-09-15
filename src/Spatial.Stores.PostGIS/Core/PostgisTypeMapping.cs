using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// Maps PostgreSQL column types to the core attribute kinds (ADR-0028,
/// architecture/distilled/contracts.md). The keys are the <c>udt_name</c> values
/// <c>information_schema.columns</c> reports; the mapping is the contract's
/// stable vocabulary — supported types become the kind the adapter reads and
/// writes, anything else fails schema discovery with an actionable diagnostic
/// that names the column and the supported types. <c>numeric</c> is mapped to
/// <see cref="AttributeKind.Double"/> (documented precision note: values
/// beyond double range lose precision — acceptable for the geometry metadata
/// role the workbench needs).
/// </summary>
internal static class PostgisTypeMapping
{
    private static readonly Dictionary<string, AttributeKind> ByUdtName = new(StringComparer.Ordinal)
    {
        ["bool"] = AttributeKind.Boolean,
        ["int2"] = AttributeKind.Int64,
        ["int4"] = AttributeKind.Int64,
        ["int8"] = AttributeKind.Int64,
        ["float4"] = AttributeKind.Double,
        ["float8"] = AttributeKind.Double,
        ["numeric"] = AttributeKind.Double,
        ["text"] = AttributeKind.String,
        ["varchar"] = AttributeKind.String,
        ["bpchar"] = AttributeKind.String,
        ["geometry"] = AttributeKind.Geometry,
        ["geography"] = AttributeKind.Geometry,
        ["timestamptz"] = AttributeKind.DateTimeOffset,
        ["timestamp"] = AttributeKind.DateTimeOffset,
        ["date"] = AttributeKind.DateTimeOffset,
        ["uuid"] = AttributeKind.Guid,
    };

    /// <summary>Tries the postgres <c>udt_name</c> → attribute kind mapping.</summary>
    public static bool TryMap(string udtName, out AttributeKind kind, out string unsupportedReason)
    {
        unsupportedReason = string.Empty;
        if (ByUdtName.TryGetValue(udtName, out kind))
        {
            return true;
        }

        kind = default;
        unsupportedReason = $"postgis@1 supports bool, int2/4/8, float4/8, numeric, text/varchar, geometry/geography, timestamptz/timestamp/date and uuid columns; '{udtName}' is not one of them.";
        return false;
    }

    /// <summary>The SQL column type for one field of a creating batch (dataset.create).</summary>
    public static string SqlType(AttributeKind kind) => kind switch
    {
        AttributeKind.Boolean => "boolean",
        AttributeKind.Int64 => "bigint",
        AttributeKind.Double => "double precision",
        AttributeKind.String => "text",
        AttributeKind.DateTimeOffset => "timestamptz",
        AttributeKind.Guid => "uuid",
        AttributeKind.Geometry => "geometry",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"no SQL type for attribute kind {kind}"),
    };
}
