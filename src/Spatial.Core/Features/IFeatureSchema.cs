using System.Diagnostics.CodeAnalysis;

namespace Spatial.Core.Features;

/// <summary>
/// The contract face of a feature schema (ADR-0029): an ordered set of
/// field definitions with the append-only decodability rule. Clients that
/// resolve fields, validate batches or evolve schemas may bind this
/// interface; <see cref="FeatureSchema"/> is the immutable implementation.
/// </summary>
public interface IFeatureSchema
{
    /// <summary>The fields in declaration order.</summary>
    IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>The field count.</summary>
    int Count { get; }

    /// <summary>The field at a position.</summary>
    FieldDefinition this[int index] { get; }

    /// <summary>Zero-based index of the field, or -1 when the schema has no such name.</summary>
    int IndexOf(string name);

    /// <summary>Whether this schema (the reader) can decode values written under <paramref name="writer"/>.</summary>
    bool IsDecodableFrom(IFeatureSchema writer);

    /// <summary>Like <see cref="IsDecodableFrom"/>, but reports the first incompatibility through <paramref name="reason"/>.</summary>
    bool TryIsDecodableFrom(IFeatureSchema writer, [NotNullWhen(false)] out string? reason);
}
