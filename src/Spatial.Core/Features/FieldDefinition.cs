namespace Spatial.Core.Features;

/// <summary>
/// The declaration of one attribute column: name, value kind (never
/// <see cref="AttributeKind.Null"/>), nullability and an optional description.
/// Equality includes the description.
/// </summary>
public readonly record struct FieldDefinition
{
    public FieldDefinition(string name, AttributeKind kind, bool nullable = false, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (kind == AttributeKind.Null)
        {
            throw new ArgumentException(
                $"A field cannot have the reserved {nameof(AttributeKind.Null)} kind; use the nullable flag instead.", nameof(kind));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unknown attribute kind {(byte)kind}.");
        }

        if (description is not null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A field description must be null or a non-whitespace string.", nameof(description));
        }

        Name = name;
        Kind = kind;
        Nullable = nullable;
        Description = description;
    }

    public string Name { get; }

    public AttributeKind Kind { get; }

    public bool Nullable { get; }

    public string? Description { get; }

    /// <summary>
    /// Whether a value written under <paramref name="writer"/> can be read as
    /// this field: names and kinds must match, and a reader must accept at
    /// least the nulls a writer can produce (<c>reader.Nullable</c> implies
    /// <c>writer.Nullable</c> rows are decodable).
    /// </summary>
    public bool IsDecodableFrom(FieldDefinition writer) =>
        Name == writer.Name && Kind == writer.Kind && (!writer.Nullable || Nullable);

    public override string ToString() => Nullable ? $"{Name} ({Kind}, nullable)" : $"{Name} ({Kind})";
}
