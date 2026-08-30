using System.Diagnostics.CodeAnalysis;

namespace Spatial.Core.Features;

/// <summary>
/// An ordered set of field definitions. Field names are unique (ordinal,
/// case-sensitive). A reader schema is decodable from a writer schema when
/// the writer's columns are the reader's columns plus optional appended
/// columns — the append-only column evolution rule — with matching names,
/// kinds and compatible nullability per field.
/// </summary>
public sealed class FeatureSchema : IEquatable<FeatureSchema>
{
    private readonly FieldDefinition[] _fields;

    public FeatureSchema(IEnumerable<FieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var list = fields as FieldDefinition[] ?? fields.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in list)
        {
            if (!seen.Add(field.Name))
            {
                throw new ArgumentException($"Duplicate field name '{field.Name}' in feature schema.", nameof(fields));
            }
        }

        _fields = list;
    }

    /// <summary>The fields in declaration order.</summary>
    public IReadOnlyList<FieldDefinition> Fields => _fields;

    public int Count => _fields.Length;

    public FieldDefinition this[int index] => _fields[index];

    /// <summary>Zero-based index of the field, or -1 when the schema has no such name.</summary>
    public int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _fields.Length; i++)
        {
            if (_fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether this schema (the reader) can decode values written under
    /// <paramref name="writer"/>: the writer's fields must start with exactly
    /// this schema's fields (same order, names and kinds), and writer
    /// nullability must not exceed reader nullability.
    /// </summary>
    public bool IsDecodableFrom(FeatureSchema writer) => TryIsDecodableFrom(writer, out _);

    /// <summary>
    /// Like <see cref="IsDecodableFrom"/>, but reports the first incompatibility
    /// through <paramref name="reason"/> for actionable diagnostics.
    /// </summary>
    public bool TryIsDecodableFrom(FeatureSchema writer, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (Count > writer.Count)
        {
            reason = $"reader schema declares {Count} fields but the writer schema declares only {writer.Count} (columns may only be appended).";
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            var readerField = _fields[i];
            var writerField = writer._fields[i];
            if (readerField.Name != writerField.Name)
            {
                reason = $"field {i}: reader expects '{readerField.Name}' but the writer declares '{writerField.Name}'.";
                return false;
            }

            if (readerField.Kind != writerField.Kind)
            {
                reason = $"field {i} '{readerField.Name}': reader kind {readerField.Kind} does not match writer kind {writerField.Kind}.";
                return false;
            }

            if (writerField.Nullable && !readerField.Nullable)
            {
                reason = $"field {i} '{readerField.Name}': the writer may produce null but the reader requires non-null.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public bool Equals(FeatureSchema? other)
    {
        if (other is null || other._fields.Length != _fields.Length)
        {
            return false;
        }

        for (var i = 0; i < _fields.Length; i++)
        {
            if (_fields[i] != other._fields[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is FeatureSchema other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var field in _fields)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"FeatureSchema[{string.Join(", ", _fields.Select(field => field.Name))}]";
}
