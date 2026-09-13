using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Core.Features;

namespace Spatial.PluginSdk;

/// <summary>
/// JSON converter for <see cref="FieldDefinition"/> (ADR-0033): the core
/// field type is an immutable struct with validation, so the typed API
/// converts it explicitly rather than relying on constructor binding.
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
