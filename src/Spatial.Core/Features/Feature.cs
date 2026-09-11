namespace Spatial.Core.Features;

/// <summary>
/// A feature: identity, schema and one attribute value per declared field.
/// Immutable: the attribute list is defensively copied, and every value is
/// validated against its field (count, kind and nullability) at construction.
/// </summary>
public sealed class Feature : IFeature, IEquatable<Feature>
{
    private readonly AttributeValue[] _attributes;

    public Feature(FeatureId id, FeatureSchema schema, IReadOnlyList<AttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(attributes);

        Id = id;
        Schema = schema;

        var values = attributes.ToArray();
        if (values.Length != schema.Count)
        {
            throw new ArgumentException(
                $"Feature '{id}' has {values.Length} attributes but its schema declares {schema.Count} fields.", nameof(attributes));
        }

        for (var i = 0; i < values.Length; i++)
        {
            ValidateValue(id, schema, i, values[i]);
        }

        _attributes = values;
    }

    public FeatureId Id { get; }

    public FeatureSchema Schema { get; }

    IFeatureSchema IFeature.Schema => Schema;

    /// <summary>The attributes, in schema field order.</summary>
    public IReadOnlyList<AttributeValue> Attributes => _attributes;

    public AttributeValue this[int index] => _attributes[index];

    public AttributeValue this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            var index = Schema.IndexOf(name);
            if (index < 0)
            {
                var fields = string.Join(", ", Schema.Fields.Select(field => $"'{field.Name}'"));
                throw new ArgumentException($"Feature '{Id}' has no attribute named '{name}'. Fields: {fields}.", nameof(name));
            }

            return _attributes[index];
        }
    }

    public bool Equals(Feature? other) => other is not null && ContentEquals(other);

    public override bool Equals(object? obj) => obj is Feature other && ContentEquals(other);

    private bool ContentEquals(Feature other)
    {
        if (!Id.Equals(other.Id) || !Schema.Equals(other.Schema) || _attributes.Length != other._attributes.Length)
        {
            return false;
        }

        for (var i = 0; i < _attributes.Length; i++)
        {
            if (_attributes[i] != other._attributes[i])
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Schema);
        foreach (var attribute in _attributes)
        {
            hash.Add(attribute);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"Feature {Id} ({Schema})";

    private static void ValidateValue(FeatureId id, FeatureSchema schema, int index, AttributeValue value)
    {
        var field = schema[index];
        if (value.Kind == AttributeKind.Null)
        {
            if (!field.Nullable)
            {
                throw new ArgumentException($"Feature '{id}' attribute {index} ('{field.Name}'): null value in a non-nullable field.");
            }

            return;
        }

        if (value.Kind != field.Kind)
        {
            throw new ArgumentException(
                $"Feature '{id}' attribute {index} ('{field.Name}'): value kind {value.Kind} does not match field kind {field.Kind}.");
        }
    }
}
