using System.Runtime.ExceptionServices;
using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// Shapes wire-boundary failures into capability results. Separate from
/// <see cref="WorkerProvider"/> so the proxy stays focused on forwarding:
/// only the four wire-failure kinds map to shaped errors; anything else is an
/// internal bug and is rethrown with its original stack.
/// </summary>
internal static class InvokeFailureMapper
{
    public static CapabilityFailure For(
        CapabilityInvocation invocation,
        ProviderId providerId,
        Exception exception)
    {
        if (exception is ValueCodecException { Message: var message })
        {
            return ContractViolationResult(invocation, message);
        }

        if (exception is OperationCanceledException)
        {
            return CapabilityResult.Failure(DeadlineOrCancelledError(invocation));
        }

        return ProviderBoundaryFailure(invocation, providerId, exception);
    }

    private static CapabilityFailure ProviderBoundaryFailure(
        CapabilityInvocation invocation,
        ProviderId providerId,
        Exception exception)
    {
        switch (exception)
        {
            case WorkerDisconnectedException { Message: var message }:
                return ProviderFailureResult(
                    $"worker {providerId} disconnected while serving {invocation.Capability}: {message}");
            case WorkerProtocolException { Message: var message }:
                return ProviderFailureResult(
                    $"worker {providerId} violated the protocol while serving {invocation.Capability}: {message}");
            default:
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw new InvalidOperationException("unreachable: Throw rethrows the original exception");
        }
    }

    private static CapabilityError DeadlineOrCancelledError(CapabilityInvocation invocation)
    {
        if (invocation.Deadline is { } due && DateTimeOffset.UtcNow >= due)
        {
            return CapabilityError.DeadlineExceeded(invocation.Capability);
        }

        return CapabilityError.Cancelled(invocation.Capability);
    }

    private static CapabilityFailure ContractViolationResult(CapabilityInvocation invocation, string message) =>
        CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"an argument of {invocation.Capability} cannot cross the worker boundary: {message}"));

    private static CapabilityFailure ProviderFailureResult(string message) =>
        CapabilityResult.Failure(CapabilityError.ProviderFailure(message));

    /// <summary>Shapes a wire outcome into a capability result; a result without a shaped error becomes a provider failure.</summary>
    public static CapabilityResult ShapeResult(CapabilityInvocation invocation, WorkerOutcome outcome) =>
        outcome.Kind == WorkerOutcomeKind.Success
            ? CapabilityResult.Success(outcome.Value)
            : CapabilityResult.Failure(outcome.Error ?? CapabilityError.ProviderFailure(
                $"an invocation of {invocation.Capability} completed without a shaped error"));

    /// <summary>The result for a worker that cannot serve right now.</summary>
    public static CapabilityResult Unavailable(CapabilityInvocation invocation, WorkerInstance instance) =>
        CapabilityResult.Failure(CapabilityError.ProviderUnavailable(
            $"worker {instance.ProviderId} is {instance.State} and cannot serve {invocation.Capability} right now"
            + (instance.LastError is null ? string.Empty : $"; last error: {instance.LastError}")));
}
