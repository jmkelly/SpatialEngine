using System.Text;
using System.Text.Json;
using Spatial.Esri.Codec;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The per-feature edit result codec (spec §9.1.6–§9.1.9, ADR-0037): the
/// <c>{objectId, globalId, success}</c> success shape and the
/// <c>{success:false, error:{code,description}}</c> failure shape, written
/// under the caller-named result array.
/// </summary>
public sealed class EsriEditResultTests
{
    [Fact]
    public void A_successful_result_carries_the_object_id_and_null_global_id()
    {
        var json = Write("addResults", [EsriEditResult.Succeeded(42)]);

        var result = json.GetProperty("addResults")[0];
        Assert.Equal(42, result.GetProperty("objectId").GetInt64());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("globalId").ValueKind);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.False(result.TryGetProperty("error", out _));
    }

    [Fact]
    public void A_failed_result_carries_a_null_object_id_and_an_error()
    {
        var json = Write("updateResults", [EsriEditResult.Failed(400, "The feature is invalid.")]);

        var result = json.GetProperty("updateResults")[0];
        Assert.Equal(JsonValueKind.Null, result.GetProperty("objectId").ValueKind);
        Assert.False(result.GetProperty("success").GetBoolean());
        var error = result.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Equal("The feature is invalid.", error.GetProperty("description").GetString());
    }

    [Fact]
    public void A_global_id_is_written_as_a_string()
    {
        var globalId = Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF");
        var json = Write("deleteResults", [EsriEditResult.Succeeded(7, globalId)]);

        Assert.Equal(globalId, json.GetProperty("deleteResults")[0].GetProperty("globalId").GetGuid());
    }

    private static JsonElement Write(string name, IReadOnlyList<EsriEditResult> results)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            EsriEditResultCodec.Write(writer, name, results);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement.Clone();
    }
}
