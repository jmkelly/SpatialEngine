using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The outcome of deterministic resolution (plan §9): the winning provider,
/// the exact descriptor that matched, its health, and the step that selected
/// it.
/// </summary>
public sealed record ResolvedProvider(
    ICapabilityProvider Provider,
    CapabilityDescriptor Descriptor,
    ProviderHealth Health,
    ResolutionStep Step)
{
    public override string ToString() =>
        $"{Descriptor.Id} → {Provider.Id} via {Step} ({Health})";
}
