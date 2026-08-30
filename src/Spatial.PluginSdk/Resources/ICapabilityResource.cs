using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The read-only view of a runtime-owned resource the registry returns: the
/// opaque identity, kind, owning provider, current state and creation time.
/// Clients hold the immutable <see cref="ResourceHandle"/> for transfer; the
/// registry answers state and lifecycle questions through this surface.
/// </summary>
public interface ICapabilityResource
{
    ResourceId Id { get; }

    ResourceKind Kind { get; }

    /// <summary>The provider that created (and owns) the resource.</summary>
    ProviderId Owner { get; }

    ResourceState State { get; }

    DateTimeOffset CreatedAt { get; }
}
