namespace Spatial.Core.Features;

/// <summary>
/// The contract face of one field declaration (ADR-0029): name, value kind,
/// nullability and description, plus the per-field decodability rule.
/// <see cref="FieldDefinition"/> is the implementing value.
/// </summary>
public interface IFieldDefinition
{
    /// <summary>The field name.</summary>
    string Name { get; }

    /// <summary>The value kind (never <see cref="AttributeKind.Null"/>).</summary>
    AttributeKind Kind { get; }

    /// <summary>Whether the field may contain <see cref="AttributeValue.Null"/>.</summary>
    bool Nullable { get; }

    /// <summary>An optional human description.</summary>
    string? Description { get; }

    /// <summary>Whether a value written under <paramref name="writer"/> can be read as this field.</summary>
    bool IsDecodableFrom(IFieldDefinition writer);
}
