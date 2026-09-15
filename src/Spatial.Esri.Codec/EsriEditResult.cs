using System.Text.Json;

namespace Spatial.Esri.Codec;

/// <summary>
/// The per-feature edit result of the Feature Service editing operations
/// (spec §9.1.6–§9.1.9):
/// <c>{"objectId": &lt;id&gt;, "globalId": null, "success": true}</c>, or on
/// failure <c>{"success": false, "error": {"code": &lt;n&gt;, "description": "..."}}</c>.
/// A feature that failed carries a null object id and an error; a feature
/// that succeeded carries the object id the service assigned.
/// </summary>
public sealed record EsriEditResult(long? ObjectId, Guid? GlobalId, bool Success, EsriEditResultError? Error = null)
{
    /// <summary>A successful edit for the assigned or existing object id.</summary>
    public static EsriEditResult Succeeded(long? objectId, Guid? globalId = null) => new(objectId, globalId, true);

    /// <summary>A failed edit with no object id and the reason.</summary>
    public static EsriEditResult Failed(int code, string description) =>
        new(null, null, false, new EsriEditResultError(code, description));
}

/// <summary>The per-feature edit failure inside <see cref="EsriEditResult"/>.</summary>
public sealed record EsriEditResultError(int Code, string Description);

/// <summary>
/// Writes the edit-result arrays. The property name differs per operation
/// (<c>addResults</c>, <c>updateResults</c>, <c>deleteResults</c>), so the
/// caller names the array and the codec owns the per-result shape.
/// </summary>
public static class EsriEditResultCodec
{
    /// <summary>Writes <c>"name": [ {objectId, globalId, success[, error]} ]</c>.</summary>
    public static void Write(Utf8JsonWriter writer, string name, IReadOnlyList<EsriEditResult> results)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(results);

        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var result in results)
        {
            WriteResult(writer, result);
        }

        writer.WriteEndArray();
    }

    private static void WriteResult(Utf8JsonWriter writer, EsriEditResult result)
    {
        writer.WriteStartObject();
        if (result.ObjectId is { } objectId)
        {
            writer.WriteNumber("objectId", objectId);
        }
        else
        {
            writer.WriteNull("objectId");
        }

        if (result.GlobalId is { } globalId)
        {
            writer.WriteString("globalId", globalId);
        }
        else
        {
            writer.WriteNull("globalId");
        }

        writer.WriteBoolean("success", result.Success);
        if (result.Error is { } error)
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", error.Code);
            writer.WriteString("description", error.Description);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
