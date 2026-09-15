using System.Globalization;
using Spatial.Core.Features;

namespace Spatial.Ingest.Codec;

/// <summary>
/// Turns parsed raw records into a core schema and canonical feature pages.
/// Field order is the union of source attribute names in first-seen order
/// with the geometry field appended last (the same canonical placement the
/// ArcGIS REST provider uses). Value kinds widen across the document:
/// int + double becomes double, and any string or mixed boolean/number
/// becomes string; a missing or null value makes the field nullable.
/// </summary>
internal static class IngestSchema
{
    /// <summary>Infers the schema of a parsed document.</summary>
    public static FeatureSchema Infer(RawFeatureSet set, string geometryField)
    {
        var fields = new List<FieldDefinition>(set.Names.Count + 1);
        foreach (var name in set.Names)
        {
            fields.Add(InferField(name, set.Features));
        }

        fields.Add(new FieldDefinition(geometryField, AttributeKind.Geometry, nullable: true));
        return new FeatureSchema(fields);
    }

    /// <summary>Builds the canonical pages, synthesising a source identity when the document had none.</summary>
    public static IReadOnlyList<FeatureBatch> Build(RawFeatureSet set, FeatureSchema schema, int batchSize)
    {
        var pages = new List<FeatureBatch>();
        var buffer = new List<Feature>(batchSize);
        for (var index = 0; index < set.Features.Count; index++)
        {
            buffer.Add(BuildFeature(set.Features[index], schema, index));
            if (buffer.Count == batchSize)
            {
                pages.Add(new FeatureBatch(schema, buffer.ToArray()));
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            pages.Add(new FeatureBatch(schema, buffer.ToArray()));
        }

        return pages;
    }

    private static FieldDefinition InferField(string name, IReadOnlyList<RawFeature> features)
    {
        AttributeKind? kind = null;
        var nullable = false;
        foreach (var feature in features)
        {
            if (feature.Properties.TryGetValue(name, out var value) is false || value is null)
            {
                nullable = true;
                continue;
            }

            var valueKind = KindOf(value);
            kind = kind is null ? valueKind : Widen(kind.Value, valueKind);
        }

        return kind is null
            ? new FieldDefinition(name, AttributeKind.String, nullable: true)
            : new FieldDefinition(name, kind.Value, nullable);
    }

    private static Feature BuildFeature(RawFeature raw, FeatureSchema schema, int index)
    {
        var values = new AttributeValue[schema.Count];
        for (var field = 0; field < schema.Count; field++)
        {
            var definition = schema[field];
            if (definition.Kind == AttributeKind.Geometry)
            {
                values[field] = raw.Geometry is null ? AttributeValue.Null : AttributeValue.FromGeometry(raw.Geometry);
                continue;
            }

            values[field] = raw.Properties.TryGetValue(definition.Name, out var value) && value is not null
                ? ToAttribute(value, definition.Kind)
                : AttributeValue.Null;
        }

        return new Feature(new FeatureId(Identity(raw, index)), schema, values);
    }

    private static string Identity(RawFeature raw, int index) =>
        raw.Id is { Length: > 0 } id ? id : (index + 1).ToString(CultureInfo.InvariantCulture);

    private static AttributeValue ToAttribute(object value, AttributeKind kind)
    {
        if (kind == AttributeKind.Boolean)
        {
            return AttributeValue.FromBoolean((bool)value);
        }

        if (kind == AttributeKind.Int64)
        {
            return AttributeValue.FromInt64(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        if (kind == AttributeKind.Double)
        {
            return AttributeValue.FromDouble(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }

        return AttributeValue.FromString(System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static AttributeKind KindOf(object value)
    {
        if (value is bool)
        {
            return AttributeKind.Boolean;
        }

        if (value is long)
        {
            return AttributeKind.Int64;
        }

        return value is double ? AttributeKind.Double : AttributeKind.String;
    }

    private static AttributeKind Widen(AttributeKind current, AttributeKind next)
    {
        if (current == next)
        {
            return current;
        }

        if (current == AttributeKind.String || next == AttributeKind.String)
        {
            return AttributeKind.String;
        }

        if (IsNumeric(current) && IsNumeric(next))
        {
            return AttributeKind.Double;
        }

        return AttributeKind.String;
    }

    private static bool IsNumeric(AttributeKind kind) => kind is AttributeKind.Int64 or AttributeKind.Double;
}
