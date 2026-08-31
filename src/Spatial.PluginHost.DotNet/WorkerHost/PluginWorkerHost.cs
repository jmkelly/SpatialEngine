using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;

namespace Spatial.PluginHost.DotNet.WorkerHost;

/// <summary>
/// The worker-side protocol loop: announces the validated package
/// (<c>hello</c>), serves <c>invoke</c>/<c>cancel</c>/<c>ping</c>/<c>close</c>
/// against a loaded <see cref="ICapabilityProvider"/>, relays progress and
/// results over the wire and enforces deadlines host-side too (a silent
/// worker can never hang a job past its deadline). Invocations run
/// concurrently; <c>close</c> (drain) stops accepting new work, waits for
/// in-flight invocations and then answers <c>closed</c>.
/// </summary>
public sealed class PluginWorkerHost : IAsyncDisposable
{
    private readonly WorkerChannel _channel;
    private readonly PluginManifest _manifest;
    private readonly ICapabilityProvider _provider;
    private readonly WireFacilities _facilities;
    private readonly ConcurrentDictionary<string, InvokeState> _invokes = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _draining;

    private PluginWorkerHost(WorkerChannel channel, PluginManifest manifest, ICapabilityProvider provider)
    {
        _channel = channel;
        _manifest = manifest;
        _provider = provider;
        _facilities = new WireFacilities(channel);
    }

    /// <summary>
    /// Creates the host over the given streams (the worker process's stdin/
    /// stdout) and starts its reader loop.
    /// </summary>
    public static PluginWorkerHost Start(
        Stream input,
        Stream output,
        PluginManifest manifest,
        ICapabilityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(provider);
        PluginWorkerHost host = null!;
        var channel = new WorkerChannel(
            input,
            output,
            envelope => host.HandleAsync(envelope),
            $"host:{provider.Id}");
        host = new PluginWorkerHost(channel, manifest, provider);
        return host;
    }

    /// <summary>
    /// Announces readiness and runs until the supervisor drains the worker
    /// (<c>close</c>) or the channel disconnects.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _channel.SendAsync(WorkerProtocol.Hello, WorkerPayload.Hello(_manifest));
        await Task.WhenAny(_drained.Task, _channel.Disconnected).WaitAsync(cancellationToken);
    }

    /// <summary>Whether the worker is draining (no new invocations accepted).</summary>
    public bool IsDraining => _draining;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _channel.DisposeAsync();
        _shutdown.Dispose();
    }

    private async ValueTask HandleAsync(WorkerEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case WorkerProtocol.Invoke:
                await HandleInvokeAsync(envelope);
                break;
            case WorkerProtocol.Cancel:
                HandleCancel(envelope.Id);
                break;
            case WorkerProtocol.Ping:
                await _channel.SendAsync(WorkerProtocol.Pong, envelope.Payload?.DeepClone(), envelope.Id);
                break;
            case WorkerProtocol.Close:
                await HandleCloseAsync();
                break;
            default:
                await _channel.SendAsync(
                    WorkerProtocol.Error,
                    WorkerPayload.ProtocolError($"the worker received an unexpected message type '{envelope.Type}'"));
                break;
        }
    }

    private async ValueTask HandleInvokeAsync(WorkerEnvelope envelope)
    {
        var invokeId = envelope.Id;
        if (invokeId is null)
        {
            await FailProtocolAsync("an invoke message must carry an id (the invoke id)");
            return;
        }

        if (_draining)
        {
            await SendResultAsync(invokeId, CapabilityResult.Failure(CapabilityError.ProviderUnavailable(
                $"the worker {_provider.Id} is draining; no new invocations are accepted")));
            return;
        }

        if (TryBuildInvocation(envelope.Payload, invokeId, out var invocation, out var parseError))
        {
            StartInvocation(invokeId, invocation);
        }
        else
        {
            await SendResultAsync(invokeId, CapabilityResult.Failure(
                CapabilityError.ContractViolation(parseError ?? $"the invoke payload for {invokeId} is malformed")));
        }
    }

    /// <summary>Starts the provider invocation off the read loop with the invocation's
    /// own deadline watch; the caller (run loop) reports the outcome.</summary>
    private void StartInvocation(string invokeId, CapabilityInvocation invocation)
    {
        var cts = CreateDeadlineCts(invocation, _shutdown.Token);
        var effective = invocation with { CancellationToken = cts.Token };
        var state = new InvokeState(cts);
        // Run the provider on the thread pool: the provider may block
        // synchronously on a facility RPC (GetResult), and that must never
        // stall the read loop that answers the RPC.
        state.Task = Task.Factory.StartNew(
            () => RunInvokeAsync(invokeId, effective, cts),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        _invokes[invokeId] = state;
    }

    private static CancellationTokenSource CreateDeadlineCts(
        CapabilityInvocation invocation,
        CancellationToken shutdown)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (invocation.Deadline is { } due)
        {
            var remaining = due - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                cts.Cancel();
            }
            else
            {
                cts.CancelAfter(remaining);
            }
        }

        return cts;
    }

    private void HandleCancel(string? invokeId)
    {
        if (invokeId is null)
        {
            return;
        }

        CancelIfRunning(invokeId);
    }

    private void CancelIfRunning(string invokeId)
    {
        if (_invokes.TryGetValue(invokeId, out var state))
        {
            state.Cancel();
        }
    }

    private async Task RunInvokeAsync(string invokeId, CapabilityInvocation invocation, CancellationTokenSource cts)
    {
        try
        {
            var result = await _provider.InvokeAsync(invocation);
            await SendResultAsync(invokeId, result);
        }
        catch (OperationCanceledException)
        {
            await SendResultAsync(invokeId, CapabilityResult.Failure(CancellationError(invocation)));
        }
        catch (Exception exception)
        {
            await SendResultAsync(invokeId, CapabilityResult.Failure(CapabilityError.ProviderFailure(
                $"the worker provider threw {exception.GetType().Name} while serving {invocation.Capability}: {exception.Message}")));
        }
        finally
        {
            _invokes.TryRemove(invokeId, out _);
            cts.Dispose();
            TryFinishDrain();
        }
    }

    private static CapabilityError CancellationError(CapabilityInvocation invocation) =>
        invocation.Deadline is { } due && DateTimeOffset.UtcNow >= due
            ? CapabilityError.DeadlineExceeded(invocation.Capability)
            : CapabilityError.Cancelled(invocation.Capability);

    private async ValueTask HandleCloseAsync()
    {
        _draining = true;
        if (_invokes.IsEmpty)
        {
            await FinishCloseAsync();
            return;
        }

        await Task.WhenAll(_invokes.Values.Select(state => state.Task));
        await FinishCloseAsync();
    }

    private async ValueTask FinishCloseAsync()
    {
        await _channel.SendAsync(WorkerProtocol.Closed, new JsonObject { ["inFlight"] = _invokes.Count });
        _drained.TrySetResult();
    }

    private void TryFinishDrain()
    {
        if (_draining && _invokes.IsEmpty)
        {
            _ = FinishCloseAsync().AsTask();
        }
    }

    private async ValueTask SendResultAsync(string invokeId, CapabilityResult result)
    {
        await _channel.SendAsync(
            WorkerProtocol.Result,
            result is CapabilityFailure failure
                ? WorkerPayload.ResultFailure(failure.Error)
                : WorkerPayload.ResultSuccess(((CapabilitySuccess)result).Value),
            invokeId);
    }

    private async ValueTask FailProtocolAsync(string message)
    {
        await _channel.SendAsync(WorkerProtocol.Error, WorkerPayload.ProtocolError(message));
    }

    private bool TryBuildInvocation(
        JsonNode? payload,
        string invokeId,
        out CapabilityInvocation invocation,
        out string? error)
    {
        invocation = null!;
        error = null;
        if (payload is not JsonObject obj)
        {
            error = $"the invoke payload for {invokeId} is not an object";
            return false;
        }

        if (!CapabilityId.TryParse(obj["capability"]?.GetValue<string>(), out var capability))
        {
            error = $"the invoke payload for {invokeId} must carry a valid 'capability'";
            return false;
        }

        if (!TryReadArguments(obj["arguments"] as JsonObject, out var arguments, out error))
        {
            return false;
        }

        if (!TryReadPermissions(obj["permissions"] as JsonArray, out var permissions, out error))
        {
            return false;
        }

        if (!TryReadDeadline(obj["deadline"], out var deadline, out error))
        {
            return false;
        }

        var progress = new ProgressRelay(_channel, invokeId);
        invocation = new CapabilityInvocation(
            capability,
            arguments,
            permissions,
            deadline,
            progress,
            CancellationToken.None,
            _facilities);
        return true;
    }

    private static bool TryReadArguments(
        JsonObject? argumentsNode,
        out IReadOnlyDictionary<string, object?> arguments,
        out string? error)
    {
        var map = new Dictionary<string, object?>();
        arguments = map;
        error = null;
        if (argumentsNode is null)
        {
            return true;
        }

        foreach (var entry in argumentsNode)
        {
            try
            {
                map.Add(entry.Key, ValueCodec.Decode(entry.Value));
            }
            catch (ValueCodecException exception)
            {
                error = $"the argument '{entry.Key}' cannot cross the worker boundary: {exception.Message}";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPermissions(
        JsonArray? permissionsNode,
        out IReadOnlySet<Permission> permissions,
        out string? error)
    {
        var granted = new HashSet<Permission>();
        permissions = granted;
        if (permissionsNode is null)
        {
            error = null;
            return true;
        }

        return FillPermissions(granted, permissionsNode, out error);
    }

    private static bool FillPermissions(HashSet<Permission> granted, JsonArray permissionsNode, out string? error)
    {
        foreach (var node in permissionsNode)
        {
            if (!Permission.TryParse(node?.GetValue<string>(), out var permission))
            {
                error = $"'{node?.ToJsonString()}' is not a valid permission name";
                return false;
            }

            granted.Add(permission);
        }

        error = null;
        return true;
    }

    private static bool TryReadDeadline(JsonNode? deadlineNode, out DateTimeOffset? deadline, out string? error)
    {
        deadline = null;
        error = null;
        if (deadlineNode is null)
        {
            return true;
        }

        var text = deadlineNode.GetValue<string>();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var due))
        {
            deadline = due;
            return true;
        }

        error = $"'{text}' is not a valid ISO-8601 deadline";
        return false;
    }

    private sealed class InvokeState(CancellationTokenSource cancellation)
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel() => cancellation.Cancel();
    }

    private sealed class ProgressRelay(WorkerChannel channel, string invokeId) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value)
        {
            try
            {
                _ = channel.SendAsync(WorkerProtocol.Progress, WorkerPayload.Progress(value), invokeId).AsTask();
            }
            catch (WorkerProtocolException)
            {
                // The supervisor is gone; the invoke itself will report the disconnect.
            }
        }
    }
}
