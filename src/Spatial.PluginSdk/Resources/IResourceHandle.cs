using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The read surface of a runtime-owned resource handle (ADR-0022): the
/// minted id, its kind, the owning provider and when it was created.
/// Consumers of minted resources can program against this abstraction; the
/// runtime hands out the immutable <see cref="ResourceHandle"/>.
/// </summary>
public interface IResourceHandle
{
    /// <summary>The opaque id the runtime assigned to this resource.</summary>
    ResourceId Id { get; }

    /// <summary>The dotted lowercase kind of the resource.</summary>
    ResourceKind Kind { get; }

    /// <summary>The provider that created (and owns) the resource.</summary>
    ProviderId Owner { get; }

    /// <summary>When the runtime minted the handle.</summary>
    DateTimeOffset CreatedAt { get; }
}
