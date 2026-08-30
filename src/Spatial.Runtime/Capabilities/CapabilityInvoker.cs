using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The provider invocation boundary: guards every call so providers can never
/// escape with raw exceptions, null results or ill-formed failures. Used by
/// <see cref="CapabilityRuntime"/> so the routing facade stays small.
/// </summary>
internal static class CapabilityInvoker
{
    /// <summary>
    /// Invokes the provider and converts every failure shape into a structured
    /// <see cref="CapabilityResult"/>: unhandled exceptions become
    /// <see cref="CapabilityErrorKind.ProviderFailure"/>; null results and
    /// failures without an error become
    /// <see cref="CapabilityErrorKind.ContractViolation"/>; cancellation
    /// becomes <see cref="CapabilityErrorKind.Cancelled"/> or
    /// <see cref="CapabilityErrorKind.DeadlineExceeded"/> once the deadline
    /// has passed.
    /// </summary>
    public static async Task<CapabilityResult> InvokeSafelyAsync(
        ResolvedProvider resolved,
        CapabilityInvocation invocation)
    {
        try
        {
            var result = await resolved.Provider.InvokeAsync(invocation);
            if (result is CapabilityFailure { Error: null })
            {
                return CapabilityResult.Failure(CapabilityError.ContractViolation(
                    $"Provider {resolved.Provider.Id} returned a failure without an error for {invocation.Capability}."));
            }

            return result ?? CapabilityResult.Failure(CapabilityError.ContractViolation(
                $"Provider {resolved.Provider.Id} returned no result for {invocation.Capability}; "
                + "providers must return CapabilitySuccess or CapabilityFailure."));
        }
        catch (OperationCanceledException)
        {
            return invocation.Deadline is { } due && DateTimeOffset.UtcNow >= due
                ? CapabilityResult.Failure(CapabilityError.DeadlineExceeded(invocation.Capability))
                : CapabilityResult.Failure(CapabilityError.Cancelled(invocation.Capability));
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(CapabilityError.ProviderFailure(
                $"Provider {resolved.Provider.Id} threw {exception.GetType().Name} while serving {invocation.Capability}: {exception.Message}"));
        }
    }
}
