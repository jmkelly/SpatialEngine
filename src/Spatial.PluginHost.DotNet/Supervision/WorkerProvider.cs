using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The host-side view of a supervised worker: an <see cref="ICapabilityProvider"/>
/// that forwards invocations over the worker's wire channel (ADR-0025). The
/// descriptor surface comes from the validated manifest; the invocation
/// encodes arguments, relays progress, forwards cancellation as a
/// <c>cancel</c> message and maps the worker's outcome back into a
/// <see cref="CapabilityResult"/> — including a crash, which becomes an
/// actionable <see cref="CapabilityErrorKind.ProviderFailure"/> naming the
/// worker. The proxy is stable across restarts: it follows the
/// <see cref="WorkerInstance"/>'s current channel.
/// </summary>
public sealed class WorkerProvider : ICapabilityProvider
{
    private readonly WorkerInstance _instance;
    private readonly ProviderId _id;
    private readonly IReadOnlyList<CapabilityDescriptor> _descriptors;

    internal WorkerProvider(WorkerInstance instance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _id = instance.ProviderId;
        _descriptors = instance.BuildDescriptors();
    }

    public ProviderId Id => _id;

    public IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;

    public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (CurrentChannel() is not { } channel)
        {
            return InvokeFailureMapper.Unavailable(invocation, _instance);
        }

        try
        {
            return await InvokeOnChannelAsync(channel, invocation);
        }
        catch (Exception exception)
        {
            return InvokeFailureMapper.For(invocation, _instance.ProviderId, exception);
        }
    }

    private WorkerChannel? CurrentChannel()
    {
        var channel = _instance.Channel;
        if (channel is null || !WorkerStateSupport.CanServe(_instance.State))
        {
            return null;
        }

        return channel;
    }

    private async ValueTask<CapabilityResult> InvokeOnChannelAsync(
        WorkerChannel channel,
        CapabilityInvocation invocation)
    {
        var payload = WorkerPayload.Invoke(
            invocation.Capability.ToString(),
            invocation.Arguments,
            invocation.GrantedPermissions.Select(permission => permission.Name),
            invocation.Deadline);
        var invokeId = Guid.NewGuid().ToString("N");
        _instance.Relay?.Begin(invokeId, invocation.Progress);
        try
        {
            using var cancellation = invocation.CancellationToken.Register(() => SendCancel(channel, invokeId));
            var response = await channel.RequestAsync(
                WorkerProtocol.Invoke,
                payload,
                WorkerProtocol.Result,
                timeout: null,
                id: invokeId,
                cancellationToken: invocation.CancellationToken);
            return InvokeFailureMapper.ShapeResult(invocation, WorkerPayload.ReadOutcome(response));
        }
        finally
        {
            _instance.Relay?.End(invokeId);
        }
    }

    private static void SendCancel(WorkerChannel channel, string invokeId)
    {
        try
        {
            _ = channel.SendAsync(WorkerProtocol.Cancel, null, invokeId).AsTask();
        }
        catch (WorkerProtocolException)
        {
            // The worker is already gone; the request observes the disconnect.
        }
    }
}
