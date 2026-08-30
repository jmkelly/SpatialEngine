namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The read-only declaration surface of a provider: its stable identity and
/// the capability descriptors it serves. The registry and resolution use
/// only this surface; invocation adds
/// <see cref="ICapabilityProvider.InvokeAsync"/>. The split lets hosts treat
/// a provider's declaration without coupling to its behaviour (and lets
/// conformance harnesses consume declarations).
/// </summary>
public interface ICapabilityCatalog
{
    /// <summary>Stable provider identity, for example <c>nts@1</c>.</summary>
    ProviderId Id { get; }

    /// <summary>The capability descriptors this provider serves, one per capability id.</summary>
    IReadOnlyList<CapabilityDescriptor> Descriptors { get; }
}
