using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The opaque token a provider returns for a runtime-owned resource (plan
/// §8): the minted id, its kind, the provider that owns it and when it was
/// created. Handles are immutable values — the runtime's registry tracks the
/// actual state (open, leased, closed) behind the id, so a client can never
/// re-create or forge a usable handle.
/// </summary>
public sealed record ResourceHandle
{
    public ResourceHandle(ResourceId id, ResourceKind kind, ProviderId owner, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(kind.Name);
        Id = id;
        Kind = kind;
        Owner = owner;
        CreatedAt = createdAt;
    }

    /// <summary>The opaque id the runtime assigned to this resource.</summary>
    public ResourceId Id { get; }

    /// <summary>The dotted lowercase kind of the resource.</summary>
    public ResourceKind Kind { get; }

    /// <summary>The provider that created (and owns) the resource.</summary>
    public ProviderId Owner { get; }

    /// <summary>When the runtime minted the handle.</summary>
    public DateTimeOffset CreatedAt { get; }

    public override string ToString() => $"{Kind} (owner {Owner})";
}
