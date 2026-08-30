namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The runtime's view of a provider's ability to serve requests. Resolution
/// (plan §9 step 4) considers <see cref="Healthy"/> and
/// <see cref="Degraded"/> providers usable — healthy first by stable
/// provider id — and skips <see cref="Unhealthy"/> ones.
/// </summary>
public enum ProviderHealth
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}

public static class ProviderHealthSupport
{
    /// <summary>Whether a provider with this health may be selected to serve.</summary>
    public static bool CanServe(ProviderHealth health) => health is ProviderHealth.Healthy or ProviderHealth.Degraded;
}