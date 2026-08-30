using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// One provider as the registry tracks it: the implementation, its declared
/// descriptors and its current health. Immutable — health changes produce a
/// new registration via <see cref="WithHealth"/>.
/// </summary>
public sealed record ProviderRegistration(
    ProviderId Id,
    ICapabilityProvider Provider,
    IReadOnlyList<CapabilityDescriptor> Descriptors,
    ProviderHealth Health)
{
    public ProviderRegistration WithHealth(ProviderHealth health) => this with { Health = health };

    public bool CanServe => ProviderHealthSupport.CanServe(Health);

    public override string ToString() => $"{Id} ({Health})";
}
