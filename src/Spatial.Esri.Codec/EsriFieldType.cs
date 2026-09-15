using Spatial.Core.Features;

namespace Spatial.Esri.Codec;

/// <summary>
/// The GeoServices <c>esriFieldType*</c> names and their core
/// <see cref="AttributeKind"/> mapping (spec §9.1.1). The engine model is a
/// superset, so only the common types are mapped; unsupported types are
/// rejected rather than guessed.
/// </summary>
public static class EsriFieldType
{
    public const string Oid = "esriFieldTypeOID";
    public const string SmallInteger = "esriFieldTypeSmallInteger";
    public const string Integer = "esriFieldTypeInteger";
    public const string Single = "esriFieldTypeSingle";
    public const string Double = "esriFieldTypeDouble";
    public const string String = "esriFieldTypeString";
    public const string Date = "esriFieldTypeDate";
    public const string Guid = "esriFieldTypeGUID";
    public const string GlobalId = "esriFieldTypeGlobalID";
    public const string Geometry = "esriFieldTypeGeometry";

    /// <summary>Maps an Esri field type name to a core attribute kind, or false when unsupported.</summary>
    public static bool TryToAttributeKind(string? esriType, out AttributeKind kind) =>
        TryToAttributeKindCore(esriType, out kind, out _);

    /// <summary>Maps an Esri field type name to a core attribute kind, reporting why an unsupported name failed.</summary>
    public static bool TryToAttributeKind(string? esriType, out AttributeKind kind, out string? error) =>
        TryToAttributeKindCore(esriType, out kind, out error);

    private static bool TryToAttributeKindCore(string? esriType, out AttributeKind kind, out string? error)
    {
        if (esriType is not null && Kinds.TryGetValue(esriType, out kind))
        {
            error = null;
            return true;
        }

        kind = AttributeKind.Null;
        error = $"Esri field type '{esriType ?? "nothing"}' has no core attribute kind.";
        return false;
    }

    /// <summary>Maps a core attribute kind to its Esri field type name.</summary>
    public static string FromAttributeKind(AttributeKind kind) =>
        Names.TryGetValue(kind, out var name)
            ? name
            : throw EsriInteropException.Invalid($"Attribute kind {kind} has no Esri field type.");

    private static readonly Dictionary<string, AttributeKind> Kinds = new(StringComparer.Ordinal)
    {
        [Oid] = AttributeKind.Int64,
        [SmallInteger] = AttributeKind.Int64,
        [Integer] = AttributeKind.Int64,
        [Single] = AttributeKind.Double,
        [Double] = AttributeKind.Double,
        [String] = AttributeKind.String,
        [Date] = AttributeKind.DateTimeOffset,
        [Guid] = AttributeKind.Guid,
        [GlobalId] = AttributeKind.Guid,
        [Geometry] = AttributeKind.Geometry,
    };

    private static readonly Dictionary<AttributeKind, string> Names = new()
    {
        [AttributeKind.Boolean] = SmallInteger,
        [AttributeKind.Int64] = Integer,
        [AttributeKind.Double] = Double,
        [AttributeKind.String] = String,
        [AttributeKind.Geometry] = Geometry,
        [AttributeKind.DateTimeOffset] = Date,
        [AttributeKind.Guid] = GlobalId,
    };
}
