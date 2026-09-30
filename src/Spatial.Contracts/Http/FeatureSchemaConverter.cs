using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Core.Features;

namespace Spatial.Contracts;

/// <summary>
/// JSON converter for <see cref="FeatureSchema"/> (ADR-0033).
///
/// <para>
/// This converter is load-bearing, not decoration. <see cref="FeatureSchema"/>
/// has no parameterless constructor and exposes <c>Fields</c> read-only, so
/// System.Text.Json cannot bind it: the constructor parameter <c>fields</c>
/// matches no settable property, and without this converter STJ throws
/// <see cref="InvalidOperationException"/> the first time the options resolve
/// the contract's metadata — which may be an unrelated call sharing those
/// options. Deleting the converter does not fail the build, and the failure
/// does not name the converter.
/// </para>
///
/// <para>
/// Guarded by <c>Spatial.Architecture.Tests.SchemaBindingGuardTests</c>
/// (registration) and by the catalogue round trip in
/// <c>tests/unit/Spatial.Client.Tests</c> (behaviour). The wire shape is
/// <c>{"fields":[...]}</c>.
/// </para>
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
