using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Esri.Codec;

/// <summary>
/// How to render one feature as an Esri JSON object (query options).
/// <c>ReturnEnvelope</c> (spec §9.1.4, 11.4+) writes each geometry as its
/// <c>{xmin..ymax}</c> envelope instead of the full shape; it only applies
/// when <c>ReturnGeometry</c> is true.
/// </summary>
public sealed record EsriFeatureWriteOptions(
    string ObjectIdField,
    long ObjectId,
    IReadOnlyList<string>? OutFields = null,
    bool ReturnGeometry = true,
    bool ReturnEnvelope = false);

/// <summary>
/// The Esri feature JSON codec (spec §9.1.4): a feature is
/// <c>{"attributes": {...}, "geometry": {...}}</c>. The engine keeps the
/// geometry inside its schema as a <c>Geometry</c> field; Esri separates it,
/// so the codec pulls it out on write and puts it back on read. Dates cross
/// as epoch milliseconds, matching the spec.
/// </summary>
public static class EsriFeatureCodec
{
    /// <summary>Writes one feature with the requested attribute projection.</summary>
    public static void Write(Utf8JsonWriter writer, IFeature feature, EsriFeatureWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(options);

        writer.WriteStartObject();
        WriteAttributes(writer, feature, options);
        if (options.ReturnGeometry)
        {
            if (options.ReturnEnvelope)
            {
                WriteEnvelope(writer, feature);
            }
            else
            {
                WriteGeometry(writer, feature);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads one feature under the layer schema. <paramref name="objectIdField"/>
    /// supplies the feature identity; the geometry field (by name, or the first
    /// <c>Geometry</c> field) receives the decoded geometry.
    /// </summary>
    public static Feature Decode(
        JsonElement element,
        FeatureSchema schema,
        string objectIdField,
        string? geometryField,
        CoordinateReference? fallback)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("An Esri feature must be a JSON object.");
        }

        var attributes = Attributes(element);
        var geometryElement = Geometry(element);
        var geometryName = ResolveGeometryField(schema, geometryField);
        var identity = ReadIdentity(attributes, objectIdField);

        var values = new AttributeValue[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            var field = schema[i];
            values[i] = field.Name == geometryName
                ? ReadGeometry(geometryElement, fallback)
                : EsriAttributeCodec.Read(attributes, field);
        }

        return new Feature(new FeatureId(identity), schema, values);
    }

    private static JsonElement Attributes(JsonElement element) =>
        element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
            ? attributes
            : default;

    private static JsonElement Geometry(JsonElement element) =>
        element.TryGetProperty("geometry", out var geometry) ? geometry : default;

    private static string ReadIdentity(JsonElement attributes, string objectIdField)
    {
        if (EsriAttributeCodec.TryGetValue(attributes, objectIdField, out var objectId)
            && objectId.ValueKind == JsonValueKind.Number
            && objectId.TryGetInt64(out var id))
        {
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        throw EsriInteropException.Invalid($"The feature carries no numeric '{objectIdField}' attribute.");
    }

    private static void WriteAttributes(Utf8JsonWriter writer, IFeature feature, EsriFeatureWriteOptions options)
    {
        writer.WritePropertyName("attributes");
        writer.WriteStartObject();
        writer.WriteNumber(options.ObjectIdField, options.ObjectId);
        var schema = feature.Schema;
        for (var i = 0; i < schema.Count; i++)
        {
            var field = schema[i];
            if (field.Kind == AttributeKind.Geometry
                || field.Name == options.ObjectIdField
                || (options.OutFields is { Count: > 0 } && !options.OutFields.Contains(field.Name, StringComparer.Ordinal)))
            {
                continue;
            }

            EsriAttributeCodec.Write(writer, field.Name, feature[i]);
        }

        writer.WriteEndObject();
    }

    private static void WriteGeometry(Utf8JsonWriter writer, IFeature feature)
    {
        var geometry = FindGeometry(feature);
        if (geometry is null)
        {
            writer.WriteNull("geometry");
            return;
        }

        writer.WritePropertyName("geometry");
        EsriGeometryCodec.Write(writer, geometry);
    }

    /// <summary>
    /// Writes the feature's envelope (<c>{xmin,ymin,xmax,ymax}</c> in the
    /// geometry's CRS) instead of its full geometry. A feature with no (or
    /// an empty) geometry writes <c>"geometry": null</c>, exactly as the
    /// full-geometry form does.
    /// </summary>
    private static void WriteEnvelope(Utf8JsonWriter writer, IFeature feature)
    {
        var geometry = FindGeometry(feature);
        if (geometry?.Envelope is not { } envelope || envelope.IsEmpty)
        {
            writer.WriteNull("geometry");
            return;
        }

        writer.WritePropertyName("geometry");
        writer.WriteStartObject();
        writer.WriteNumber("xmin", envelope.MinX);
        writer.WriteNumber("ymin", envelope.MinY);
        writer.WriteNumber("xmax", envelope.MaxX);
        writer.WriteNumber("ymax", envelope.MaxY);
        EsriSpatialReference.Write(writer, geometry.CoordinateReference);
        writer.WriteEndObject();
    }

    private static IGeometry? FindGeometry(IFeature feature)
    {
        var schema = feature.Schema;
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry && feature[i].Kind == AttributeKind.Geometry)
            {
                return feature[i].GeometryValue;
            }
        }

        return null;
    }

    private static string? ResolveGeometryField(FeatureSchema schema, string? geometryField)
    {
        if (geometryField is not null)
        {
            return geometryField;
        }

        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return schema[i].Name;
            }
        }

        return null;
    }

    private static AttributeValue ReadGeometry(JsonElement geometry, CoordinateReference? fallback)
    {
        if (geometry.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return AttributeValue.Null;
        }

        return AttributeValue.FromGeometry(EsriGeometryCodec.Decode(geometry, fallback));
    }
}
