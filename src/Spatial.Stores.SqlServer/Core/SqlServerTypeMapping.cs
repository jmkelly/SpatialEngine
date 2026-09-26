using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// Maps SQL Server column types to the core attribute kinds (ADR-0028
/// vocabulary, as the SQL Server provider follows it). The keys are the
/// <c>TYPE_NAME(user_type_id)</c> values <c>sys.columns</c> reports; the
/// mapping is the contract's stable vocabulary — supported types become the
/// kind the adapter reads and writes, anything else fails schema discovery
/// with an actionable diagnostic that names the column and the supported
/// types. <c>decimal</c>, <c>money</c> and <c>float</c> are mapped to
/// <see cref="AttributeKind.Double"/> (documented precision note: values
/// beyond double range lose precision — acceptable for the geometry metadata
/// role the workbench needs).
/// </summary>
internal static class SqlServerTypeMapping
{
    /// <summary>The T-SQL type whose value carries a geometry (planar) or geography (geodetic) value.</summary>
    public const string GeometryType = "geometry";

    /// <summary>The T-SQL type whose value carries geodetic coordinates.</summary>
    public const string GeographyType = "geography";

    private static readonly Dictionary<string, AttributeKind> ByTypeName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bit"] = AttributeKind.Boolean,
        ["tinyint"] = AttributeKind.Int64,
        ["smallint"] = AttributeKind.Int64,
        ["int"] = AttributeKind.Int64,
        ["bigint"] = AttributeKind.Int64,
        ["real"] = AttributeKind.Double,
        ["float"] = AttributeKind.Double,
        ["decimal"] = AttributeKind.Double,
        ["numeric"] = AttributeKind.Double,
        ["money"] = AttributeKind.Double,
        ["smallmoney"] = AttributeKind.Double,
        ["char"] = AttributeKind.String,
        ["varchar"] = AttributeKind.String,
        ["nchar"] = AttributeKind.String,
        ["nvarchar"] = AttributeKind.String,
        ["text"] = AttributeKind.String,
        ["ntext"] = AttributeKind.String,
        ["date"] = AttributeKind.DateTimeOffset,
        ["datetime"] = AttributeKind.DateTimeOffset,
        ["datetime2"] = AttributeKind.DateTimeOffset,
        ["smalldatetime"] = AttributeKind.DateTimeOffset,
        ["datetimeoffset"] = AttributeKind.DateTimeOffset,
        ["uniqueidentifier"] = AttributeKind.Guid,
        [GeometryType] = AttributeKind.Geometry,
        [GeographyType] = AttributeKind.Geometry,
    };

    /// <summary>Whether a discovered column type carries geometry.</summary>
    public static bool IsSpatialType(string typeName) =>
        string.Equals(typeName, GeometryType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(typeName, GeographyType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tries the SQL Server type name → attribute kind mapping.</summary>
    public static bool TryMap(string typeName, out AttributeKind kind, out string unsupportedReason)
    {
        unsupportedReason = string.Empty;
        if (ByTypeName.TryGetValue(typeName, out kind))
        {
            return true;
        }

        kind = default;
        unsupportedReason =
            $"the sqlserver@1 store supports bit, tinyint/smallint/int/bigint, real/float/decimal/money, char/varchar/nvarchar/text, " +
            $"date/datetime/datetime2/datetimeoffset, uniqueidentifier and geometry/geography columns; '{typeName}' is not one of them.";
        return false;
    }

    /// <summary>The T-SQL column type for one field of a creating batch (dataset.create / ingest).</summary>
    public static string SqlType(AttributeKind kind) => kind switch
    {
        AttributeKind.Boolean => "bit",
        AttributeKind.Int64 => "bigint",
        AttributeKind.Double => "float",
        AttributeKind.String => "nvarchar(max)",
        AttributeKind.DateTimeOffset => "datetimeoffset",
        AttributeKind.Guid => "uniqueidentifier",
        AttributeKind.Geometry => GeometryType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"no T-SQL type for attribute kind {kind}"),
    };
}
