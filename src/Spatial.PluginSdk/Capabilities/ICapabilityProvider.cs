namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The provider-side contract for one capability implementation. A provider
/// declares a stable id and the capability descriptors it serves; the runtime
/// resolves and routes invocations to it. Providers are never referenced
/// from <c>Spatial.Runtime</c> — only this interface (architecture tests).
/// </summary>
public interface ICapabilityProvider
{
    /// <summary>Stable provider identity, for example <c>nts@1</c>.</summary>
    ProviderId Id { get; }

    /// <summary>The capability descriptors this provider serves, one per capability id.</summary>
    IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

    /// <summary>
    /// Serves one invocation. Must return <see cref="CapabilitySuccess"/> or
    /// <see cref="CapabilityFailure"/>; throwing is treated by the runtime as
    /// <see cref="CapabilityErrorKind.ProviderFailure"/>. Honour
    /// <see cref="CapabilityInvocation.CancellationToken"/> and report
    /// <see cref="CapabilityInvocation.Progress"/> when applicable.
    /// </summary>
    ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation);
}