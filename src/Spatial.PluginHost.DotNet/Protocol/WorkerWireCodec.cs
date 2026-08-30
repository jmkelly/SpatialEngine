using System.Text.Json.Nodes;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// The v1 envelope codec: each message is one JSON object per line, with the
/// shape <c>{"protocol":"spatial.worker/1","type":"&lt;type&gt;","id":"&lt;id|null&gt;","payload":{…}}</c>.
/// The codec enforces the protocol version and the required fields on both
/// directions, so a version skew or a malformed peer fails loudly with an
/// actionable message (ADR-0025). Encoding is canonical: the same envelope
/// always produces the same line.
/// </summary>
public static class WorkerWireCodec
{
    /// <summary>Serialises one envelope to its single-line JSON form (without the trailing newline).</summary>
    public static string Encode(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.IsProtocolV1)
        {
            throw new WorkerProtocolException(
                $"unsupported protocol '{envelope.Protocol}'; this endpoint speaks {WorkerProtocol.Version} only");
        }

        if (string.IsNullOrEmpty(envelope.Type))
        {
            throw new WorkerProtocolException("an envelope needs a message type");
        }

        var node = new JsonObject
        {
            ["protocol"] = envelope.Protocol,
            ["type"] = envelope.Type,
        };
        if (envelope.Id is { } id)
        {
            node["id"] = id;
        }

        if (envelope.Payload is { } payload)
        {
            node["payload"] = payload.DeepClone();
        }

        return node.ToJsonString();
    }

    /// <summary>
    /// Parses one line back into an envelope, throwing
    /// <see cref="WorkerProtocolException"/> for anything that is not a v1
    /// envelope (malformed JSON, wrong root shape, missing protocol/type,
    /// unknown protocol version).
    /// </summary>
    public static WorkerEnvelope Decode(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new WorkerProtocolException($"the worker line is not valid JSON: {exception.Message}", exception);
        }

        if (root is not JsonObject obj)
        {
            throw new WorkerProtocolException("a worker envelope must be a JSON object");
        }

        var protocol = ReadString(obj, "protocol");
        if (protocol != WorkerProtocol.Version)
        {
            throw new WorkerProtocolException(
                $"unsupported protocol '{protocol ?? "(missing)"}'; this endpoint speaks {WorkerProtocol.Version} only");
        }

        var type = ReadString(obj, "type");
        if (string.IsNullOrEmpty(type))
        {
            throw new WorkerProtocolException("a worker envelope is missing its 'type'");
        }

        var id = ReadString(obj, "id");
        var payload = obj["payload"]?.DeepClone();
        return new WorkerEnvelope(protocol, type, id, payload);
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        var node = obj[name];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
