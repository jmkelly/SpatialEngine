namespace Spatial.PluginSdk.Resources;

/// <summary>
/// A time-bounded right to use a resource (plan §8 "leases"): the resource
/// id, when the lease expires and how long it was granted for. The runtime's
/// resource registry issues leases against a resource, honours their expiry
/// (reading a stream requires an active lease) and reclaims resources whose
/// leases a client never released when the owning provider is disposed — a
/// client failing to release a lease is a leak test case.
/// </summary>
public sealed record ResourceLease(ResourceId ResourceId, DateTimeOffset ExpiresAt, TimeSpan Duration)
{
    /// <summary>Whether the lease is still in force at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => now < ExpiresAt;

    public override string ToString() => $"lease for {ResourceId} until {ExpiresAt:O}";
}
