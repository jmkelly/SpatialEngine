using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The provider-side minting surface for runtime-owned resources (plan §8
/// "opaque handles with ownership"): a provider creates a resource through
/// the invocation's facilities and returns the <see cref="ResourceHandle"/> as
/// its result value. The runtime tracks the handle (leases, disposal, leak
/// detection) and derives the resource-local provider from its owner during
/// resolution.
/// </summary>
public interface IResourceFactory
{
    /// <summary>Creates and registers a resource owned by the current provider.</summary>
    ResourceHandle Create(ResourceKind kind);
}
