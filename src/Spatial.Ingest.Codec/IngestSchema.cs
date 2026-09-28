using System.Globalization;
using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;

namespace Spatial.Ingest.Codec;

/// <summary>
/// Turns parsed raw records into a core schema and canonical feature pages.
/// Field order is the union of source attribute names in first-seen order with
/// the geometry field appended last (the same canonical placement the ArcGIS
/// REST provider uses). Value kinds widen across the document: int + double
/// becomes double, and any string or mixed boolean/number becomes string; a
/// missing or null value makes the field nullable. The kinds actually observed
/// are kept, because "the first row was null so the column is a string" and
/// "the column is a string" are different facts about the same schema.
/// </summary>
internal static class IngestSchema
{
    /// <summary>Accumulates one attribute column's evidence as records arrive.</summary>
    private sealed class Evidence
    {
        public AttributeKind? Widened { get; private set; }

        public bool Nullable { get; private set; }

        public long Nulls { get; private set; }

        public List<AttributeKind> Observed { get; } = [];

        public void Observe(object? value)
        {
            if (value is null)
            {
                Nullable = true;
                Nulls++;
                return;
            }

            var kind = KindOf(value);
            if (Observed.Contains(kind) is false)
            {
                Observed.Add(kind);
            }

            Widened = Widened is null ? kind : Widen(Widened.Value, kind);
        }

        public FieldDefinition ToField(string name) =>
            Widened is null
                ? new FieldDefinition(name, AttributeKind.String, nullable: true)
                : new FieldDefinition(name, Widened.Value, Nullable);

        public InferredField ToReport(string name) =>
            new(name, Widened ?? AttributeKind.String, Nullable, Nulls, Observed.ToArray());
    }

    /// <summary>Infers the schema of the records seen so far, with its evidence.</summary>
    public static (FeatureSchema Schema, IReadOnlyList<InferredField> Inferred) Infer(
        IReadOnlyList<RawFeature> records, IReadOnlyList<string> names, string geometryField)
    {
        var fields = new List<FieldDefinition>(names.Count + 1);
        var report = new List<InferredField>(names.Count);
        foreach (var name in names)
        {
            var evidence = new Evidence();
            foreach (var record in records)
            {
                evidence.Observe(record.Properties.GetValueOrDefault(name));
            }

            fields.Add(evidence.ToField(name));
            report.Add(evidence.ToReport(name));
        }

        fields.Add(new FieldDefinition(geometryField, AttributeKind.Geometry, nullable: true));
        return (new FeatureSchema(fields), report);
    }

    /// <summary>Builds the canonical pages, synthesising a source identity when the document had none.</summary>
    public static IReadOnlyList<FeatureBatch> Build(IReadOnlyList<RawFeature> records, FeatureSchema schema, int batchSize)
    {
        var pages = new List<FeatureBatch>();
        var buffer = new List<Feature>(batchSize);
        for (var index = 0; index < records.Count; index++)
        {
            buffer.Add(BuildFeature(records[index], schema, index));
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

    /// <summary>
    /// Builds one canonical feature. Returns false when the record carries an
    /// attribute the fixed schema has no field for, which only a streaming
    /// decode can discover: the schema was inferred from a prefix, and a value
    /// dropped without saying so is exactly the silent data loss the decode
    /// report exists to end.
    /// </summary>
    public static bool TryBuildFeature(RawFeature raw, FeatureSchema schema, int index, out Feature feature, out string? unexpected)
    {
        unexpected = null;
        foreach (var property in raw.Properties.Keys)
        {
            if (schema.IndexOf(property) < 0)
            {
                unexpected = property;
                feature = null!;
                return false;
            }
        }

        feature = BuildFeature(raw, schema, index);
        return true;
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
