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
        _id = ProviderId.Parse(instance.Package.Manifest.Id);
        _descriptors = ManifestDescriptorBuilder.ToDescriptors(instance.Package.Manifest);
    }

    public ProviderId Id => _id;

    public IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;

    public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (_instance.Channel is not { } channel || !WorkerStateSupport.CanServe(_instance.State))
        {
            return Unavailable(invocation);
        }

        try
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
                return MapOutcome(invocation, WorkerPayload.ReadOutcome(response));
            }
            finally
            {
                _instance.Relay?.End(invokeId);
            }
        }
        catch (WorkerValueException exception)
        {
            return CapabilityResult.Failure(CapabilityError.ContractViolation(
                $"an argument of {invocation.Capability} cannot cross the worker boundary: {exception.Message}"));
        }
        catch (OperationCanceledException)
        {
            return CapabilityResult.Failure(
                invocation.Deadline is { } due && DateTimeOffset.UtcNow >= due
                    ? CapabilityError.DeadlineExceeded(invocation.Capability)
                    : CapabilityError.Cancelled(invocation.Capability));
        }
        catch (WorkerDisconnectedException exception)
        {
            return CapabilityResult.Failure(CapabilityError.ProviderFailure(
                $"worker {_instance.ProviderId} disconnected while serving {invocation.Capability}: {exception.Message}"));
        }
        catch (WorkerProtocolException exception)
        {
            return CapabilityResult.Failure(CapabilityError.ProviderFailure(
                $"worker {_instance.ProviderId} violated the protocol while serving {invocation.Capability}: {exception.Message}"));
        }
    }

    private static CapabilityResult MapOutcome(CapabilityInvocation invocation, WorkerOutcome outcome) =>
        outcome.Kind == WorkerOutcomeKind.Success
            ? CapabilityResult.Success(outcome.Value)
            : CapabilityResult.Failure(outcome.Error ?? CapabilityError.ProviderFailure(
                $"an invocation of {invocation.Capability} completed without a shaped error"));

    private CapabilityFailure Unavailable(CapabilityInvocation invocation) =>
        CapabilityResult.Failure(CapabilityError.ProviderUnavailable(
            $"worker {_instance.ProviderId} is {_instance.State} and cannot serve {invocation.Capability} right now"
            + (_instance.LastError is null ? string.Empty : $"; last error: {_instance.LastError}")));

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
