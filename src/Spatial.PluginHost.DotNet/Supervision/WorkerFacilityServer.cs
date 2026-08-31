using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Resources;
using Spatial.Runtime.Streams;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The supervisor side of the facility RPCs (ADR-0025): workers ask the host
/// to mint resources and bounded streams through <c>facility.mint</c> and
/// <c>facility.stream.*</c>, and this server performs the work in the host's
/// <see cref="ResourceRegistry"/> — so every handle the worker returns is a
/// real runtime-owned host handle (ADR-0022/0023), the bounded buffer lives
/// host-side (real cross-process backpressure) and draining reclaims
/// everything through the registry.
/// </summary>
public sealed class WorkerFacilityServer
{
    private readonly ResourceRegistry _resources;
    private readonly ConcurrentDictionary<Guid, BoundedStream> _streams = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public WorkerFacilityServer(ResourceRegistry resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    /// <summary>
    /// Runs one facility request off the read loop under a per-worker gate
    /// (each worker's requests stay ordered — single-writer streams). Errors
    /// are swallowed and reported through <paramref name="onFailure"/> so the
    /// loop can keep serving the worker.
    /// </summary>
    public async Task ProcessAsync(
        WorkerChannel channel,
        WorkerEnvelope envelope,
        ProviderId owner,
        Guid workerId,
        Action<Guid, string> onFailure)
    {
        var gate = _gates.GetOrAdd(workerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await HandleAsync(channel, envelope, owner);
        }
        catch (Exception exception)
        {
            onFailure(workerId, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Handles one facility request and sends the correlated <c>facility.result</c> answer.</summary>
    public async ValueTask HandleAsync(WorkerChannel channel, WorkerEnvelope envelope, ProviderId owner)
    {
        var answer = envelope.Type switch
        {
            WorkerProtocol.FacilityMint => Mint(envelope.Payload, owner),
            WorkerProtocol.FacilityStreamCreate => CreateStream(envelope.Payload, owner),
            WorkerProtocol.FacilityStreamWrite => await WriteAsync(envelope.Payload),
            WorkerProtocol.FacilityStreamComplete => Complete(envelope.Payload),
            _ => WorkerPayload.FacilityResultError(CapabilityError.ContractViolation(
                $"unknown facility request '{envelope.Type}'")),
        };
        await channel.SendAsync(WorkerProtocol.FacilityResult, answer, envelope.Id);
    }

    /// <summary>Drops the stream registry entries for a drained worker (the resources are reclaimed by the registry).</summary>
    public void Reset() => _streams.Clear();

    private JsonObject Mint(JsonNode? payload, ProviderId owner)
    {
        if (payload?["kind"]?.GetValue<string>() is not { } kind || !ResourceKind.TryParse(kind, out var resourceKind))
        {
            return WorkerPayload.FacilityResultError(CapabilityError.InvalidArguments(
                "a facility.mint request must carry a valid resource kind"));
        }

        var handle = _resources.Create(owner, resourceKind);
        return WorkerPayload.FacilityResultOk(
            handle.Id.Value.ToString("N"), handle.Kind.Name, handle.Owner.ToString(), handle.CreatedAt);
    }

    private JsonObject CreateStream(JsonNode? payload, ProviderId owner)
    {
        if (payload?["kind"]?.GetValue<string>() is not { } kind
            || !ResourceKind.TryParse(kind, out var resourceKind)
            || payload["capacity"]?.GetValue<int>() is not { } capacity
            || capacity < 1)
        {
            return WorkerPayload.FacilityResultError(CapabilityError.InvalidArguments(
                "a facility.stream.create request must carry a valid kind and a positive capacity"));
        }

        var stream = new BoundedStream(capacity);
        var handle = _resources.Create(owner, resourceKind, stream);
        _streams[handle.Id.Value] = stream;
        return WorkerPayload.FacilityResultOk(
            handle.Id.Value.ToString("N"), handle.Kind.Name, handle.Owner.ToString(), handle.CreatedAt, capacity);
    }

    private async ValueTask<JsonObject> WriteAsync(JsonNode? payload)
    {
        if (!TryGetStream(payload, out var stream))
        {
            return WorkerPayload.FacilityResultError(CapabilityError.InvalidArguments(
                "a facility.stream.write request must carry a known stream token"));
        }

        var items = payload!["items"] as JsonArray;
        return await WriteItemsAsync(stream, items);
    }

    /// <summary>Writes every decoded item to the bounded stream; a closed/cancelled stream becomes a provider-failure result.</summary>
    private static async ValueTask<JsonObject> WriteItemsAsync(BoundedStream stream, JsonArray? items)
    {
        try
        {
            foreach (var node in items ?? new JsonArray())
            {
                await stream.WriteAsync(ValueCodec.Decode(node));
            }

            return FacilityOk();
        }
        catch (Exception exception)
        {
            return WorkerPayload.FacilityResultError(CapabilityError.ProviderFailure(
                $"the stream is closed or the write cancelled: {exception.Message}"));
        }
    }

    private JsonObject Complete(JsonNode? payload)
    {
        if (!TryGetStream(payload, out var stream))
        {
            return WorkerPayload.FacilityResultError(CapabilityError.InvalidArguments(
                "a facility.stream.complete request must carry a known stream token"));
        }

        var errorNode = payload!["error"];
        stream.Complete(errorNode is null ? null : ReadStreamError(errorNode));
        if (payload!["token"]?.GetValue<string>() is { } token && Guid.TryParse(token, out var id))
        {
            _streams.TryRemove(id, out _);
        }

        return FacilityOk();
    }

    private bool TryGetStream(JsonNode? payload, out BoundedStream stream)
    {
        stream = null!;
        if (payload?["token"]?.GetValue<string>() is not { } token
            || !Guid.TryParse(token, out var id)
            || !_streams.TryGetValue(id, out var found))
        {
            return false;
        }

        stream = found;
        return true;
    }

    private static JsonObject FacilityOk() => new() { ["ok"] = true };

    private static CapabilityError ReadStreamError(JsonNode node)
    {
        var code = node["code"]?.GetValue<string>() ?? "provider.failure";
        var message = node["message"]?.GetValue<string>() ?? "the stream failed";
        var kind = Enum.TryParse<CapabilityErrorKind>(node["kind"]?.GetValue<string>(), out var parsed)
            ? parsed
            : CapabilityErrorKind.ProviderFailure;
        return new CapabilityError(kind, code, message);
    }
}
