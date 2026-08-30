using System.Globalization;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// The inline value codec of the worker protocol (ADR-0020/0025, plan §8
/// "inline values for points, envelopes, options and small geometries"): the
/// language-neutral encoding of argument and result values. The inline
/// protocol carries scalars, <c>$i64</c>-tagged 64-bit integers, base64
/// bytes and opaque resource-handle tags; it deliberately rejects spatial
/// values — those require canonical binary interchange (ADR-0020), which the
/// store/operation plugins add in later phases. The runtime therefore never
/// routes geometry through JSON between workers.
/// </summary>
public static class WorkerValueCodec
{
    /// <summary>
    /// Encodes a value to its wire form. Throws <see cref="WorkerValueException"/>
    /// for values the inline protocol does not carry (spatial values in
    /// particular, with an ADR-0020 hint).
    /// </summary>
    public static JsonNode? Encode(object? value)
    {
        return value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => Int64Node(number),
            double number => JsonValue.Create(number),
            float number => JsonValue.Create((double)number),
            string text => JsonValue.Create(text),
            byte[] bytes => BytesNode(bytes),
            ResourceHandle handle => ResourceNode(handle),
            ProviderId id => JsonValue.Create(id.ToString()),
            _ => throw Unsupported(value),
        };
    }

    /// <summary>
    /// Decodes a wire node back to a CLR value: JSON numbers become
    /// <see cref="int"/> when integral and in range, else <see cref="long"/>,
    /// else <see cref="double"/>; tagged nodes become their tagged type.
    /// </summary>
    public static object? Decode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return DecodeObject(obj);
        }

        if (node is JsonArray)
        {
            throw new WorkerValueException(
                "arrays are not supported as inline worker values; a worker value is a scalar, $i64, $bytes or $resource");
        }

        if (node is JsonValue value)
        {
            return DecodeValue(value);
        }

        throw new WorkerValueException($"unsupported worker value node {node.GetType().Name}");
    }

    private static object DecodeObject(JsonObject obj)
    {
        if (obj.TryGetPropertyValue("$i64", out var i64Node) && i64Node is JsonValue i64Value)
        {
            return ReadInt64(i64Value, "$i64");
        }

        if (obj.TryGetPropertyValue("$bytes", out var bytesNode) && bytesNode is JsonValue bytesValue)
        {
            return ReadBytes(bytesValue, "$bytes");
        }

        if (obj.TryGetPropertyValue("$resource", out var resourceNode) && resourceNode is JsonObject resource)
        {
            return ReadResource(resource);
        }

        throw new WorkerValueException(
            "object values are not supported as inline worker values; "
            + "remember to tag 64-bit integers ($i64), bytes ($bytes) and resource handles ($resource)");
    }

    private static object DecodeValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var longNumber))
        {
            return longNumber;
        }

        if (value.TryGetValue<double>(out var doubleNumber))
        {
            return doubleNumber;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new WorkerValueException(
            $"the JSON value '{value.ToJsonString()}' is not a supported inline worker value");
    }

    private static JsonObject Int64Node(long number) =>
        new() { ["$i64"] = number.ToString(CultureInfo.InvariantCulture) };

    private static JsonObject BytesNode(byte[] bytes) =>
        new() { ["$bytes"] = Convert.ToBase64String(bytes) };

    private static JsonObject ResourceNode(ResourceHandle handle) => new()
    {
        ["$resource"] = new JsonObject
        {
            ["token"] = handle.Id.Value.ToString("N"),
            ["kind"] = handle.Kind.Name,
            ["owner"] = handle.Owner.ToString(),
            ["createdAt"] = handle.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        },
    };

    private static long ReadInt64(JsonValue node, string tag)
    {
        if (node.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new WorkerValueException(
            $"the {tag} tag must carry a decimal string, for example {{\"{tag}\":\"80\"}}");
    }

    private static byte[] ReadBytes(JsonValue node, string tag)
    {
        try
        {
            return Convert.FromBase64String(node.GetValue<string>());
        }
        catch (FormatException exception)
        {
            throw new WorkerValueException($"the {tag} tag must carry a base64 string: {exception.Message}", exception);
        }
    }

    private static ResourceHandle ReadResource(JsonObject resource)
    {
        try
        {
            var token = resource["token"]?.GetValue<string>();
            var kind = resource["kind"]?.GetValue<string>();
            var owner = resource["owner"]?.GetValue<string>();
            var createdAt = resource["createdAt"]?.GetValue<string>();
            if (token is null || kind is null || owner is null || createdAt is null)
            {
                throw new WorkerValueException("the $resource tag must carry token, kind, owner and createdAt");
            }

            var handle = new ResourceHandle(
                new ResourceId(Guid.Parse(token)),
                ResourceKind.Parse(kind),
                ProviderId.Parse(owner),
                DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            return handle;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or WorkerValueException)
        {
            throw new WorkerValueException($"the $resource tag is malformed: {exception.Message}", exception);
        }
    }

    private static WorkerValueException Unsupported(object value)
    {
        var type = value.GetType();
        var hint = type.FullName?.StartsWith("Spatial.Core.", StringComparison.Ordinal) == true
            ? " Spatial values cross the worker boundary as canonical binary interchange (ADR-0020), not through the inline JSON protocol."
            : string.Empty;
        return new WorkerValueException(
            $"'{type.FullName}' is not supported by the inline worker protocol; supported values are "
            + "null, bool, int32, int64 ($i64), double, string, byte[] ($bytes), ResourceHandle ($resource) and ProviderId."
            + hint);
    }
}
