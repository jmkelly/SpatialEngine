using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;

namespace Spatial.Interop.Esri;

/// <summary>
/// The attribute half of the Esri feature JSON codec: core
/// <see cref="AttributeValue"/> ↔ JSON scalar per the field's
/// <see cref="AttributeKind"/>. Dates cross as epoch milliseconds (spec
/// §9.1.1); unknown shapes are rejected rather than coerced.
/// </summary>
public static class EsriAttributeCodec
{
    /// <summary>Writes a named attribute value.</summary>
    public static void Write(Utf8JsonWriter writer, string name, AttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(name);
        WriteValue(writer, value);
    }

    /// <summary>Writes an attribute value without its name.</summary>
    public static void WriteValue(Utf8JsonWriter writer, AttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value.Kind)
        {
            case AttributeKind.Null:
                writer.WriteNullValue();
                break;
            case AttributeKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue);
                break;
            case AttributeKind.Int64:
                writer.WriteNumberValue(value.Int64Value);
                break;
            case AttributeKind.Double:
                writer.WriteNumberValue(value.DoubleValue);
                break;
            case AttributeKind.String:
                writer.WriteStringValue(value.StringValue);
                break;
            case AttributeKind.DateTimeOffset:
                writer.WriteNumberValue(value.DateTimeOffsetValue.ToUnixTimeMilliseconds());
                break;
            case AttributeKind.Guid:
                writer.WriteStringValue(value.GuidValue);
                break;
            case AttributeKind.Geometry:
                writer.WriteNullValue();
                break;
            default:
                throw EsriInteropException.Invalid($"Attribute kind {value.Kind} has no Esri JSON encoding.");
        }
    }

    /// <summary>
    /// Reads the named attribute in the shape its field kind requires. A
    /// missing or null property is <see cref="AttributeValue.Null"/> (the
    /// feature constructor enforces nullability).
    /// </summary>
    public static AttributeValue Read(JsonElement attributes, IFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return TryGetValue(attributes, field.Name, out var element)
            ? ReadValue(field, element)
            : AttributeValue.Null;
    }

    private static bool TryGetValue(JsonElement attributes, string name, out JsonElement element)
    {
        element = default;
        return attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty(name, out element)
            && element.ValueKind != JsonValueKind.Null;
    }

    private static AttributeValue ReadValue(IFieldDefinition field, JsonElement element) => field.Kind switch
    {
        AttributeKind.Boolean => ReadBoolean(element),
        AttributeKind.Int64 => AttributeValue.FromInt64(ReadInt64(element)),
        AttributeKind.Double => AttributeValue.FromDouble(ReadDouble(element)),
        AttributeKind.String => AttributeValue.FromString(ReadString(element)),
        AttributeKind.DateTimeOffset => AttributeValue.FromDateTimeOffset(ReadDate(element)),
        AttributeKind.Guid => AttributeValue.FromGuid(ReadGuid(element)),
        _ => throw EsriInteropException.Invalid($"Field '{field.Name}' has kind {field.Kind}, which cannot be read from Esri JSON attributes."),
    };

    private static AttributeValue ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => AttributeValue.FromBoolean(true),
        JsonValueKind.False => AttributeValue.FromBoolean(false),
        JsonValueKind.Number when element.TryGetInt64(out var number) => AttributeValue.FromBoolean(number != 0),
        _ => throw EsriInteropException.Invalid("A boolean attribute must be true, false, 0 or 1."),
    };

    private static long ReadInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : throw EsriInteropException.Invalid("An integer attribute must be a JSON number.");

    private static double ReadDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : throw EsriInteropException.Invalid("A double attribute must be a JSON number.");

    private static string ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : throw EsriInteropException.Invalid("A string attribute must be a JSON string.");

    private static DateTimeOffset ReadDate(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
        JsonValueKind.String when DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
        _ => throw EsriInteropException.Invalid("A date attribute must be epoch milliseconds or an ISO-8601 string."),
    };

    private static Guid ReadGuid(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var value)
            ? value
            : throw EsriInteropException.Invalid("A GUID attribute must be a GUID string.");
}
