using System.Text.Json.Nodes;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// One line of the worker wire protocol: a protocol version, a message type,
/// an optional correlation id and the message payload as a JSON node. <c>Id</c>
/// correlates request/response pairs (invoke↔result, ping↔pong,
/// facility.*↔facility.result) and identifies asynchronous messages (progress
/// and cancel carry the invoke id). Immutable.
/// </summary>
public sealed record WorkerEnvelope(string Protocol, string Type, string? Id, JsonNode? Payload)
{
    /// <summary>Whether this envelope speaks the protocol this host understands.</summary>
    public bool IsProtocolV1 => Protocol == WorkerProtocol.Version;

    public override string ToString() => $"{Type} (id {(Id is null ? "none" : Id)})";
}
