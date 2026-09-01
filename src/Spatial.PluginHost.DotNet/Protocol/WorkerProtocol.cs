namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// The versioned worker wire protocol (ADR-0025): a protocol version string
/// and the message type names of the language-neutral protocol the process
/// supervisor and worker host speak over stdin/stdout. Messages are
/// line-delimited JSON envelopes (see <see cref="WorkerWireCodec"/> and
/// <c>architecture/distilled/plugins.md</c>); the type names here are part of
/// the v1 wire contract and must not change without a schema version bump.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>The protocol version every envelope carries; version 1 is the initial wire contract.</summary>
    public const string Version = "spatial.worker/1";

    /// <summary>Worker → supervisor: announces the loaded package and its validated manifest (startup handshake).</summary>
    public const string Hello = "hello";

    /// <summary>Supervisor → worker: liveness probe (health checks).</summary>
    public const string Ping = "ping";

    /// <summary>Worker → supervisor: liveness answer, echoing the ping id.</summary>
    public const string Pong = "pong";

    /// <summary>Supervisor → worker: one capability invocation (id = the invoke id).</summary>
    public const string Invoke = "invoke";

    /// <summary>Worker → supervisor: a progress observation for a running invocation (id = the invoke id).</summary>
    public const string Progress = "progress";

    /// <summary>Worker → supervisor: the terminal outcome of an invocation (id = the invoke id).</summary>
    public const string Result = "result";

    /// <summary>Supervisor → worker: request cancellation of an invocation (id = the invoke id).</summary>
    public const string Cancel = "cancel";

    /// <summary>Supervisor → worker: graceful drain; the worker finishes in-flight work and replies <see cref="Closed"/>.</summary>
    public const string Close = "close";

    /// <summary>Worker → supervisor: the worker has finished its in-flight work and is exiting.</summary>
    public const string Closed = "closed";

    /// <summary>Either direction: a protocol-level error (malformed message, unknown type).</summary>
    public const string Error = "error";

    /// <summary>Worker → supervisor: facility request — mint a runtime-owned resource (id = the facility id).</summary>
    public const string FacilityMint = "facility.mint";

    /// <summary>Worker → supervisor: facility request — create a bounded stream (id = the facility id).</summary>
    public const string FacilityStreamCreate = "facility.stream.create";

    /// <summary>Worker → supervisor: facility request — write items to a bounded stream (id = the facility id).</summary>
    public const string FacilityStreamWrite = "facility.stream.write";

    /// <summary>Worker → supervisor: facility request — complete a bounded stream, optionally with an error.</summary>
    public const string FacilityStreamComplete = "facility.stream.complete";

    /// <summary>Supervisor → worker: the answer to any facility request (id echoes the facility id).</summary>
    public const string FacilityResult = "facility.result";

    /// <summary>Response envelope types: an incoming envelope with a pending id resolves the request.</summary>
    public static readonly IReadOnlyList<string> ResponseTypes =
        [Pong, Result, FacilityResult];

    /// <summary>Whether <paramref name="type"/> is a known message type.</summary>
    public static bool IsKnown(string type) =>
        type is Hello or Ping or Pong or Invoke or Progress or Result or Cancel
            or Close or Closed or Error or FacilityMint or FacilityStreamCreate
            or FacilityStreamWrite or FacilityStreamComplete or FacilityResult;
}
