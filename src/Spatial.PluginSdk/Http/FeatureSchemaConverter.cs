using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Core.Features;

namespace Spatial.PluginSdk.Http;

/// <summary>
/// JSON converter for <see cref="FeatureSchema"/> (ADR-0033): the core
/// schema type has no parameterless constructor, so the typed API converts
/// it explicitly as <c>{"fields":[...]}</c>.
/// </summary>
public sealed class FeatureSchemaConverter : JsonConverter<FeatureSchema>
{
    public override FeatureSchema? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var fields = document.RootElement.GetProperty("fields").Deserialize<IReadOnlyList<FieldDefinition>>(options);
        return new FeatureSchema(fields ?? []);
    }

    public override void Write(Utf8JsonWriter writer, FeatureSchema value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, value.Fields, options);
        writer.WriteEndObject();
    }
}
