using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spatial.PluginSdk.Http;

/// <summary>
/// Shared JSON options for the typed host API (ADR-0033): camelCase
/// everywhere so host, OpenAPI and clients stay in lockstep. Lives with the
/// SDK contracts it serialises rather than with the wire DTOs.
/// </summary>
public static class HostApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new FeatureSchemaConverter(), new FieldDefinitionConverter() },
    };
}
