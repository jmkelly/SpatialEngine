using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Stores.ArcGisRest;

/// <summary>
/// Pure mapping between ArcGIS REST JSON and core values (ADR-0035): layer
/// metadata → dataset summaries/descriptions, Esri feature JSON → canonical
/// features, Esri errors → <see cref="SpatialException"/>, dataset-id and
/// WKID conversions. Keeping it separate from <see cref="ArcGisRestStore"/>
/// leaves the store as HTTP and pagination orchestration only.
/// </summary>
internal static class ArcGisRestMapper
{
    internal const string GeometryFieldName = "geometry";

    private const string DatasetPrefix = "arcgis.l";

    private static readonly string[] LayerProperties = ["layers", "tables"];

    public static string DatasetId(int layerId) => $"{DatasetPrefix}{layerId.ToString(CultureInfo.InvariantCulture)}";

    public static int ParseLayerId(string dataset)
    {
        if (dataset is not null
            && dataset.StartsWith(DatasetPrefix, StringComparison.Ordinal)
            && int.TryParse(dataset.AsSpan(DatasetPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var layerId))
        {
            return layerId;
        }

        throw SpatialException.BadArguments($"'{dataset}' is not an ArcGIS layer dataset id (expected 'arcgis.l<layerId>').");
    }

    public static IEnumerable<JsonElement> Layers(JsonElement root)
    {
        foreach (var property in LayerProperties)
        {
            foreach (var layer in LayerElements(root, property))
            {
                yield return layer;
            }
        }
    }

    public static string LayerName(JsonElement layer, int layerId) =>
        layer.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : $"l{layerId}";

    public static int PageSize(JsonElement metadata) =>
        metadata.TryGetProperty("maxRecordCount", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out var value) && value > 0
            ? Math.Min(value, 1000)
            : 1000;

    public static DatasetSummary Summary(int layerId, JsonElement metadata) =>
        new(DatasetId(layerId), "arcgis", $"l{layerId.ToString(CultureInfo.InvariantCulture)}", GeometryFieldName, Srid(metadata), 0);

    public static DatasetDescription Describe(int layerId, JsonElement metadata)
    {
        var objectIdField = ObjectIdField(metadata);
        return new DatasetDescription(
            DatasetId(layerId),
            "arcgis",
            $"l{layerId.ToString(CultureInfo.InvariantCulture)}",
            GeometryFieldName,
            Srid(metadata),
            EngineGeometryType(metadata),
            0,
            [objectIdField],
            Schema(metadata));
    }

    public static List<Feature> ReadFeatures(JsonElement root, DatasetDescription description)
    {
        if (!root.TryGetProperty("features", out var featureElements) || featureElements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var objectIdField = description.IdColumns[0];
        var crs = LayerCoordinateReference(description.Srid);
        var features = new List<Feature>(featureElements.GetArrayLength());
        foreach (var element in featureElements.EnumerateArray())
        {
            features.Add(EsriFeatureCodec.Decode(element, description.Schema, objectIdField, GeometryFieldName, crs));
        }

        return features;
    }

    public static bool Exceeded(JsonElement root) =>
        root.TryGetProperty("exceededTransferLimit", out var exceeded) && exceeded.ValueKind == JsonValueKind.True;

    public static SpatialException MapError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 500;
        var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : "The ArcGIS REST service reported an error.";
        if (code == 400)
        {
            return SpatialException.BadArguments($"The ArcGIS REST service rejected the request: {message}");
        }

        return code == 404
            ? SpatialException.Missing($"The ArcGIS REST service could not find the resource: {message}")
            : SpatialException.Unavailable($"The ArcGIS REST service failed ({code}): {message}");
    }
    public static string? RenderWhere(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        if (!EsriFilterClause.TryParse(filter, out var clause, out var error))
        {
            throw SpatialException.BadArguments($"The filter could not be translated to an ArcGIS where clause: {error}.");
        }

        return clause!.ToWhere();
    }

    public static string EnvelopeSpatialReference(int srid) =>
        WkidMap.TryFromEpsg(srid, out var wkid)
            ? FormattableString.Invariant($"{{\"wkid\":{wkid}}}")
            : throw SpatialException.BadArguments($"SRID {srid} has no Esri WKID in the curated map.");

    public static bool Like(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        return Regex.IsMatch(
            value,
            "^" + Regex.Escape(pattern).Replace("%", ".*", StringComparison.Ordinal).Replace("_", ".", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static bool HasNumericId(JsonElement layer) =>
        layer.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;

    /// <summary>
    /// A MapServer group layer is a container for its sublayers, not queryable
    /// data. The corpus has plenty of them (geonames, govunits, NOAA weather);
    /// listing one as a dataset would advertise a layer whose query always
    /// fails, so they are skipped. Feature layers and tables are kept.
    /// </summary>
    private static bool IsGroupLayer(JsonElement layer) =>
        layer.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "Group Layer", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonElement> LayerElements(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var elements) || elements.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var layer in elements.EnumerateArray())
        {
            if (HasNumericId(layer) && !IsGroupLayer(layer))
            {
                yield return layer;
            }
        }
    }

    private static string ObjectIdField(JsonElement metadata)
    {
        if (metadata.TryGetProperty("objectIdField", out var objectId)
            && objectId.ValueKind == JsonValueKind.String
            && objectId.GetString() is { Length: > 0 } declared)
        {
            return declared;
        }

        var oid = FindOidField(metadata);
        if (oid is not null)
        {
            return oid;
        }

        return "OBJECTID";
    }

    private static string? FindOidField(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (TryReadOidName(field, out var name))
            {
                return name;
            }
        }

        return null;
    }

    private static bool TryReadOidName(JsonElement field, out string? name)
    {
        name = null;
        if (!HasFieldType(field, EsriFieldType.Oid) || !TryReadName(field, out var oid))
        {
            return false;
        }

        name = oid;
        return true;
    }

    /// <summary>True when the field metadata declares the given Esri field type.</summary>
    private static bool HasFieldType(JsonElement field, string expectedType) =>
        field.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == expectedType;

    /// <summary>Reads a non-empty string property, or reports the field has none.</summary>
    private static bool TryReadName(JsonElement field, out string? name)
    {
        name = null;
        if (!field.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        name = nameElement.GetString();
        return !string.IsNullOrEmpty(name);
    }

    private static FeatureSchema Schema(JsonElement metadata)
    {
        var fields = new List<FieldDefinition>();
        if (metadata.TryGetProperty("fields", out var fieldElements) && fieldElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldElements.EnumerateArray())
            {
                var definition = Field(field);
                // Esri carries geometry outside the attribute object under its own
                // field name (Shape/SHAPE/Geometry). The engine's canonical column
                // is GeometryFieldName, so drop the Esri-named geometry field and
                // expose the canonical one below.
                if (definition.Kind == AttributeKind.Geometry)
                {
                    continue;
                }

                fields.Add(definition);
            }
        }

        fields.Add(new FieldDefinition(GeometryFieldName, AttributeKind.Geometry, nullable: true));
        return new FeatureSchema(fields);
    }

    private static FieldDefinition Field(JsonElement field)
    {
        var name = field.GetProperty("name").GetString() ?? throw SpatialException.BadArguments("An ArcGIS field has no name.");
        var esriType = field.TryGetProperty("type", out var type) ? type.GetString() : null;
        if (!EsriFieldType.TryToAttributeKind(esriType, out var kind, out var error))
        {
            throw SpatialException.BadArguments($"ArcGIS field '{name}': {error}");
        }

        var nullable = !field.TryGetProperty("nullable", out var nullableElement) || nullableElement.ValueKind != JsonValueKind.False;
        return new FieldDefinition(name, kind, nullable);
    }

    private static int Srid(JsonElement metadata)
    {
        if (TryWkid(metadata, "spatialReference", out var epsg))
        {
            return epsg;
        }

        if (metadata.TryGetProperty("extent", out var extent) && TryWkid(extent, "spatialReference", out var extentEpsg))
        {
            return extentEpsg;
        }

        return 0;
    }

    private static bool TryWkid(JsonElement owner, string property, out int epsg)
    {
        epsg = 0;
        if (!owner.TryGetProperty(property, out var reference) || reference.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "wkid", "latestWkid" })
        {
            if (reference.TryGetProperty(name, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var wkid)
                && WkidMap.TryToEpsg(wkid, out epsg))
            {
                return true;
            }
        }

        return false;
    }

    private static string EngineGeometryType(JsonElement metadata)
    {
        var esriType = metadata.TryGetProperty("geometryType", out var geometryType) && geometryType.ValueKind == JsonValueKind.String
            ? geometryType.GetString()
            : null;
        return esriType switch
        {
            "esriGeometryPoint" => "Point",
            "esriGeometryMultipoint" => "MultiPoint",
            "esriGeometryPolyline" => "LineString",
            "esriGeometryPolygon" => "Polygon",
            _ => "Unknown",
        };
    }

    private static CoordinateReference? LayerCoordinateReference(int srid) => srid > 0 ? CoordinateReference.Epsg(srid) : null;
}
