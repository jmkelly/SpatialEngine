using Spatial.Core.Features;
using Spatial.Interop.Esri;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Maps engine catalogue/schema metadata onto the Esri Feature Service
/// resource shapes (spec §9.0/§9.1). Layer ids are stable indices into the
/// dataset catalogue ordered by id; the engine's string feature identity is
/// exposed to Esri clients as a synthetic integer <c>OBJECTID</c> derived
/// from the stable scan order.
/// </summary>
internal static class EsriLayerModel
{
    /// <summary>The synthetic integer object-id field every served layer advertises.</summary>
    public const string ObjectIdField = "OBJECTID";

    /// <summary>The capability string of a layer that only supports querying.</summary>
    public const string ReadOnlyCapabilities = "Query";

    /// <summary>The capability string of a layer whose store supports editing (ADR-0037).</summary>
    public const string EditableCapabilities = "Query,Create,Update,Delete";

    /// <summary>The configured maximum page size in features.</summary>
    public const int MaxRecordCount = 1000;

    /// <summary>The only query output the facade serves (T-017: GeoJSON stays honestly rejected).</summary>
    public const string SupportedQueryFormats = "JSON";

    /// <summary>
    /// The truthful query flags (T-018): pagination/orderBy honoured,
    /// distinct values and query extent served, statistics/having rejected,
    /// closed where-grammar (not standardized queries).
    /// </summary>
    public static readonly EsriAdvancedQueryCapabilities QueryCapabilities = new(
        SupportsPagination: true,
        SupportsOrderBy: true,
        SupportsStatistics: false,
        SupportsDistinct: true,
        SupportsHavingClause: false,
        SupportsReturningQueryExtent: true,
        UseStandardizedQueries: false);

    private static readonly Dictionary<string, string> GeometryTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["point"] = "esriGeometryPoint",
        ["multipoint"] = "esriGeometryMultipoint",
        ["linestring"] = "esriGeometryPolyline",
        ["multilinestring"] = "esriGeometryPolyline",
        ["line"] = "esriGeometryPolyline",
        ["linearring"] = "esriGeometryPolyline",
        ["polygon"] = "esriGeometryPolygon",
        ["multipolygon"] = "esriGeometryPolygon",
    };

    /// <summary>Maps an engine geometry-type name onto its Esri constant.</summary>
    public static string GeometryType(string engineGeometryType)
    {
        return GeometryTypeMap.TryGetValue(engineGeometryType.Trim(), out var result) ? result : "esriGeometryNull";
    }

    /// <summary>Builds the layer reference for the <c>FeatureServer</c> root.</summary>
    public static EsriLayerRef Reference(int id, string name) =>
        new(id, name, "Feature Layer");

    /// <summary>Builds the full layer metadata (spec §9.1).</summary>
    public static EsriLayer Describe(int id, DatasetDescription dataset, bool editable) =>
        new(
            10.0,
            id,
            dataset.Table,
            "Feature Layer",
            GeometryType(dataset.GeometryType),
            ObjectIdField,
            Fields(dataset, editable),
            editable ? EditableCapabilities : ReadOnlyCapabilities,
            MaxRecordCount,
            SpatialReference(dataset.Srid),
            SupportedQueryFormats,
            SupportsStatistics: false,
            SupportsAdvancedQueries: true,
            QueryCapabilities);

    /// <summary>The layer's Esri spatial reference, or null when the SRID is unknown to the map.</summary>
    public static EsriSpatialReferenceDto? SpatialReference(int srid) =>
        srid > 0 && WkidMap.TryFromEpsg(srid, out var wkid) ? new EsriSpatialReferenceDto(wkid) : null;

    /// <summary>The core EPSG identity for a layer SRID, or null when unspecified.</summary>
    public static Spatial.Core.Geometry.CoordinateReference? LayerCoordinateReference(int srid) =>
        srid > 0 ? Spatial.Core.Geometry.CoordinateReference.Epsg(srid) : null;

    internal static List<EsriField> Fields(DatasetDescription dataset, bool editable, IReadOnlyDictionary<string, EsriDomain>? domains = null)
    {
        var fields = new List<EsriField>
        {
            new(ObjectIdField, EsriFieldType.Oid, ObjectIdField, false, false),
        };
        foreach (var field in dataset.Schema.Fields)
        {
            fields.Add(new EsriField(
                field.Name,
                EsriFieldType.FromAttributeKind(field.Kind),
                field.Name,
                field.Nullable,
                editable && field.Kind != AttributeKind.Geometry,
                domains?.GetValueOrDefault(field.Name)));
        }

        return fields;
    }
}

/// <summary>The <c>FeatureServer</c> root shape (spec §9.0).</summary>
internal sealed record EsriFeatureServerRoot(
    double CurrentVersion,
    string ServiceDescription,
    bool SupportsDisconnectedEditing,
    string SupportedQueryFormats,
    string Capabilities,
    int MaxRecordCount,
    IReadOnlyList<EsriLayerRef> Layers,
    IReadOnlyList<EsriLayerRef> Tables,
    EsriAdvancedQueryCapabilities? AdvancedQueryCapabilities = null);

/// <summary>One layer reference under the <c>FeatureServer</c> root.</summary>
internal sealed record EsriLayerRef(int Id, string Name, string Type);

/// <summary>One layer's full metadata (spec §9.1).</summary>
internal sealed record EsriLayer(
    double CurrentVersion,
    int Id,
    string Name,
    string Type,
    string GeometryType,
    string ObjectIdField,
    IReadOnlyList<EsriField> Fields,
    string Capabilities,
    int MaxRecordCount,
    EsriSpatialReferenceDto? SpatialReference,
    string SupportedQueryFormats,
    bool SupportsStatistics,
    bool SupportsAdvancedQueries,
    EsriAdvancedQueryCapabilities AdvancedQueryCapabilities);

/// <summary>
/// The query flags clients branch on (spec §9.1 layer resource). Every value
/// is proved by the behaviour it names: pagination and orderBy are honoured
/// by <c>FeatureQueryEngine</c>, distinct values and query extent are served,
/// statistics/having are explicitly rejected, and the closed where-grammar is
/// not the standardized SQL the flag names.
/// </summary>
internal sealed record EsriAdvancedQueryCapabilities(
    bool SupportsPagination,
    bool SupportsOrderBy,
    bool SupportsStatistics,
    bool SupportsDistinct,
    bool SupportsHavingClause,
    bool SupportsReturningQueryExtent,
    bool UseStandardizedQueries);

/// <summary>One Esri field definition; <c>domain</c> is emitted only when the catalogue backs one (spec §13).</summary>
internal sealed record EsriField(string Name, string Type, string Alias, bool Nullable, bool Editable, EsriDomain? Domain = null);

/// <summary>The Esri spatial reference object.</summary>
internal sealed record EsriSpatialReferenceDto(int Wkid);
