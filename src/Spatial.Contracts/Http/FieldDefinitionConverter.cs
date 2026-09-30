using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Core.Features;

namespace Spatial.Contracts;

/// <summary>
/// JSON converter for <see cref="FieldDefinition"/> (ADR-0033).
///
/// <para>
/// This converter is load-bearing, and the failure without it is the quiet one.
/// <see cref="FieldDefinition"/> is an immutable struct with validation, so
/// System.Text.Json falls back to <c>default(FieldDefinition)</c> rather than
/// calling its constructor: a wire document decodes to a schema whose fields
/// are unnamed, kindless and undescribed, with no exception anywhere. An empty
/// or all-default schema is the visible symptom.
/// </para>
///
/// <para>
/// Guarded by <c>Spatial.Architecture.Tests.SchemaBindingGuardTests</c>
/// (registration) and by the catalogue round trip in
/// <c>tests/unit/Spatial.Client.Tests</c> (behaviour).
/// </para>
/// </summary>
public sealed class FieldDefinitionConverter : JsonConverter<FieldDefinition>
{
    public override FieldDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? string.Empty;
        var kind = root.TryGetProperty("kind", out var kindElement)
            ? ParseKind(kindElement, options)
            : AttributeKind.String;
        var nullable = root.TryGetProperty("nullable", out var nullableElement) && nullableElement.GetBoolean();
        string? description = root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind != JsonValueKind.Null
            ? descriptionElement.GetString()
            : null;
        return new FieldDefinition(name, kind, nullable, description);
    }

    public override void Write(Utf8JsonWriter writer, FieldDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WritePropertyName("kind");
        JsonSerializer.Serialize(writer, value.Kind, options);
        writer.WriteBoolean("nullable", value.Nullable);
        if (value.Description is null)
        {
            writer.WriteNull("description");
        }
        else
        {
            writer.WriteString("description", value.Description);
        }

        writer.WriteEndObject();
    }

    private static AttributeKind ParseKind(JsonElement element, JsonSerializerOptions options) =>
        element.ValueKind == JsonValueKind.String
            ? (AttributeKind)Enum.Parse(typeof(AttributeKind), element.GetString()!, ignoreCase: true)
            : (AttributeKind)element.GetInt32();
}
