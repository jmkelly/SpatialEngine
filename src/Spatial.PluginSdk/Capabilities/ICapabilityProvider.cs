namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The provider-side contract for one capability implementation: the
/// declaration surface (<see cref="ICapabilityCatalog"/>) plus invocation.
/// The runtime resolves and routes invocations to the provider; providers
/// are never referenced from <c>Spatial.Runtime</c> — only this interface
/// (architecture tests).
/// </summary>
public interface ICapabilityProvider : ICapabilityCatalog
{
    /// <summary>
    /// Serves one invocation. Must return <see cref="CapabilitySuccess"/> or
    /// <see cref="CapabilityFailure"/>; throwing is treated by the runtime as
    /// <see cref="CapabilityErrorKind.ProviderFailure"/>. Honour
    /// <see cref="CapabilityInvocation.CancellationToken"/> and report
    /// <see cref="CapabilityInvocation.Progress"/> when applicable.
    /// </summary>
    ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation);
}
