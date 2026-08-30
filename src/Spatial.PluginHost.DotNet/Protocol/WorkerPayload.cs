using System.Globalization;
using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// The payload builders and readers of the v1 worker protocol — the shared,
/// language-neutral message shapes both ends of the boundary use (ADR-0025,
/// <c>architecture/worker-protocol.md</c>). Senders build with the factories,
/// receivers parse with the readers, so a shape lives in exactly one place on
/// the .NET side.
/// </summary>
public static class WorkerPayload
{
    /// <summary>Builds the <see cref="WorkerProtocol.Hello"/> payload: the validated manifest plus the plugin display name.</summary>
    public static JsonObject Hello(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new JsonObject
        {
            ["schemaVersion"] = manifest.SchemaVersion,
            ["id"] = manifest.Id,
            ["displayName"] = manifest.DisplayName,
            ["runtime"] = manifest.Runtime,
            ["assembly"] = manifest.Assembly,
            ["assemblyType"] = manifest.AssemblyType,
            ["capabilities"] = CapabilitiesNode(manifest),
        };
    }

    /// <summary>Builds the <see cref="WorkerProtocol.Invoke"/> payload for a capability invocation.</summary>
    public static JsonObject Invoke(
        string capability,
        IReadOnlyDictionary<string, object?> arguments,
        IEnumerable<string> permissions,
        DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(arguments);
        var payload = new JsonObject
        {
            ["capability"] = capability,
            ["permissions"] = new JsonArray(permissions.Select(permission => (JsonNode)JsonValue.Create(permission)).ToArray()),
            ["deadline"] = deadline is { } due ? JsonValue.Create(due.ToString("O", CultureInfo.InvariantCulture)) : null,
        };
        var args = new JsonObject();
        foreach (var entry in arguments)
        {
            args[entry.Key] = WorkerValueCodec.Encode(entry.Value);
        }

        payload["arguments"] = args;
        return payload;
    }

    /// <summary>Builds a <see cref="WorkerProtocol.Result"/> success payload.</summary>
    public static JsonObject ResultSuccess(object? value) =>
        new() { ["kind"] = "success", ["value"] = WorkerValueCodec.Encode(value) };

    /// <summary>Builds a <see cref="WorkerProtocol.Result"/> failure payload.</summary>
    public static JsonObject ResultFailure(CapabilityError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new JsonObject
        {
            ["kind"] = "failure",
            ["error"] = new JsonObject
            {
                ["kind"] = error.Kind.ToString(),
                ["code"] = error.Code,
                ["message"] = error.Message,
            },
        };
    }

    /// <summary>Builds a <see cref="WorkerProtocol.Progress"/> payload.</summary>
    public static JsonObject Progress(ProgressReport report) =>
        new()
        {
            ["fraction"] = report.Fraction is { } fraction ? JsonValue.Create(fraction) : null,
            ["message"] = report.Message is { } message ? JsonValue.Create(message) : null,
        };

    /// <summary>Builds a <see cref="WorkerProtocol.Ping"/> payload.</summary>
    public static JsonObject Ping(string nonce) => new() { ["nonce"] = nonce };

    /// <summary>Builds a <see cref="WorkerProtocol.FacilityMint"/> payload.</summary>
    public static JsonObject FacilityMint(string kind) => new() { ["kind"] = kind };

    /// <summary>Builds a <see cref="WorkerProtocol.FacilityStreamCreate"/> payload.</summary>
    public static JsonObject FacilityStreamCreate(string kind, int capacity) =>
        new() { ["kind"] = kind, ["capacity"] = capacity };

    /// <summary>Builds a <see cref="WorkerProtocol.FacilityStreamWrite"/> payload.</summary>
    public static JsonObject FacilityStreamWrite(Guid token, IEnumerable<object?> items) =>
        new()
        {
            ["token"] = token.ToString("N"),
            ["items"] = new JsonArray(items.Select(item => WorkerValueCodec.Encode(item) ?? JsonValue.Create((string?)null)).ToArray()),
        };

    /// <summary>Builds a <see cref="WorkerProtocol.FacilityStreamComplete"/> payload (optional completion error).</summary>
    public static JsonObject FacilityStreamComplete(Guid token, ICapabilityError? error) =>
        new()
        {
            ["token"] = token.ToString("N"),
            ["error"] = error is null ? null : ErrorNode(error),
        };

    /// <summary>Builds a successful <see cref="WorkerProtocol.FacilityResult"/> payload (minted handle).</summary>
    public static JsonObject FacilityResultOk(string token, string kind, string owner, DateTimeOffset createdAt) =>
        new()
        {
            ["ok"] = true,
            ["handle"] = new JsonObject
            {
                ["token"] = token,
                ["kind"] = kind,
                ["owner"] = owner,
                ["createdAt"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
            },
        };

    /// <summary>Builds a failed <see cref="WorkerProtocol.FacilityResult"/> payload.</summary>
    public static JsonObject FacilityResultError(CapabilityError error) =>
        new() { ["ok"] = false, ["error"] = ErrorNode(error) };

    /// <summary>Builds an <see cref="WorkerProtocol.Error"/> payload.</summary>
    public static JsonObject ProtocolError(string message) =>
        new() { ["error"] = new JsonObject { ["message"] = message } };

    /// <summary>Reads an invoke outcome (the <see cref="WorkerProtocol.Result"/> payload) into a typed value.</summary>
    public static WorkerOutcome ReadOutcome(JsonNode? payload)
    {
        if (payload is not JsonObject obj || obj["kind"]?.GetValue<string>() is not { } kind)
        {
            throw new WorkerProtocolException("a result payload must carry a 'kind'");
        }

        if (kind == "success")
        {
            return new WorkerOutcome(WorkerOutcomeKind.Success, WorkerValueCodec.Decode(obj["value"]));
        }

        if (kind == "failure")
        {
            return new WorkerOutcome(WorkerOutcomeKind.Failure, null, ReadError(obj["error"]));
        }

        throw new WorkerProtocolException($"unknown result kind '{kind}'; expected 'success' or 'failure'");
    }

    /// <summary>Reads the <see cref="WorkerProtocol.Hello"/> payload back (the supervisor's view of the package).</summary>
    public static HelloDocument ReadHello(JsonNode? payload)
    {
        if (payload is not JsonObject obj
            || obj["id"]?.GetValue<string>() is not { } id
            || obj["capabilities"] is not JsonArray capabilities)
        {
            throw new WorkerProtocolException("a hello payload must carry an id and a capabilities array");
        }

        var declared = new List<string>();
        foreach (var node in capabilities)
        {
            var capability = node?["id"]?.GetValue<string>();
            if (capability is not null)
            {
                declared.Add(capability);
            }
        }

        return new HelloDocument(
            id,
            obj["runtime"]?.GetValue<string>(),
            obj["assembly"]?.GetValue<string>(),
            obj["assemblyType"]?.GetValue<string>(),
            obj["displayName"]?.GetValue<string>(),
            declared);
    }

    private static JsonArray CapabilitiesNode(PluginManifest manifest)
    {
        var array = new JsonArray();
        foreach (var capability in manifest.Capabilities ?? [])
        {
            array.Add(new JsonObject
            {
                ["id"] = capability.Id,
                ["purpose"] = capability.Purpose,
                ["inputSchema"] = capability.InputSchema,
                ["outputSchema"] = capability.OutputSchema,
            });
        }

        return array;
    }

    private static JsonObject ErrorNode(ICapabilityError error) =>
        new JsonObject
        {
            ["kind"] = error.Kind.ToString(),
            ["code"] = error.Code,
            ["message"] = error.Message,
        };

    private static CapabilityError ReadError(JsonNode? node)
    {
        if (node is not JsonObject obj
            || obj["code"]?.GetValue<string>() is not { } code
            || obj["message"]?.GetValue<string>() is not { } message)
        {
            throw new WorkerProtocolException("a failure result must carry an error with code and message");
        }

        var kind = obj["kind"]?.GetValue<string>();
        return new CapabilityError(
            Enum.TryParse<CapabilityErrorKind>(kind, out var parsed) ? parsed : CapabilityErrorKind.ProviderFailure,
            code,
            message);
    }
}

/// <summary>The typed outcome of an invocation read from the wire.</summary>
public sealed record WorkerOutcome(WorkerOutcomeKind Kind, object? Value = null, CapabilityError? Error = null);

/// <summary>Whether a wire outcome succeeded or failed.</summary>
public enum WorkerOutcomeKind
{
    Success = 0,
    Failure = 1,
}

/// <summary>The supervisor's read of a worker's hello: identity, hints and the declared capability ids.</summary>
public sealed record HelloDocument(
    string Id,
    string? Runtime,
    string? Assembly,
    string? AssemblyType,
    string? DisplayName,
    IReadOnlyList<string> Capabilities);
