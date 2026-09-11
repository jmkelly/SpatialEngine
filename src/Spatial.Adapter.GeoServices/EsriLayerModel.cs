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

    /// <summary>Maps an engine geometry-type name onto its Esri constant.</summary>
    public static string GeometryType(string engineGeometryType) => engineGeometryType.Trim().ToLowerInvariant() switch
    {
        "point" => "esriGeometryPoint",
        "multipoint" => "esriGeometryMultipoint",
        "linestring" or "multilinestring" or "line" or "linearring" => "esriGeometryPolyline",
        "polygon" or "multipolygon" => "esriGeometryPolygon",
        _ => "esriGeometryNull",
    };

    /// <summary>Builds the layer reference for the <c>FeatureServer</c> root.</summary>
    public static EsriLayerRef Reference(int id, DatasetSummary dataset) =>
        new(id, dataset.Table, "Feature Layer");

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
            SpatialReference(dataset.Srid));

    /// <summary>The layer's Esri spatial reference, or null when the SRID is unknown to the map.</summary>
    public static EsriSpatialReferenceDto? SpatialReference(int srid) =>
        srid > 0 && WkidMap.TryFromEpsg(srid, out var wkid) ? new EsriSpatialReferenceDto(wkid) : null;

    /// <summary>The core EPSG identity for a layer SRID, or null when unspecified.</summary>
    public static Spatial.Core.Geometry.CoordinateReference? LayerCoordinateReference(int srid) =>
        srid > 0 ? Spatial.Core.Geometry.CoordinateReference.Epsg(srid) : null;

    private static List<EsriField> Fields(DatasetDescription dataset, bool editable)
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
                editable && field.Kind != AttributeKind.Geometry));
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
    IReadOnlyList<EsriLayerRef> Tables);

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
    EsriSpatialReferenceDto? SpatialReference);

/// <summary>One Esri field definition.</summary>
internal sealed record EsriField(string Name, string Type, string Alias, bool Nullable, bool Editable);

/// <summary>The Esri spatial reference object.</summary>
internal sealed record EsriSpatialReferenceDto(int Wkid);
