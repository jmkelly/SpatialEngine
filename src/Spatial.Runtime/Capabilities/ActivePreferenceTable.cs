using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The runtime's *active* provider preferences (plan §10.4 "route new work to
/// the new version", Phase 5 supervision): which provider should serve a
/// capability right now, driven by the process supervisor during side-by-side
/// activation and rollback — as opposed to the immutable startup
/// <see cref="CapabilityConfiguration"/>. The active preference is consulted
/// between the resource-local step and the configured preference, so a
/// supervisor can flip new work to a freshly activated worker version without
/// touching the immutable configuration. Soft, like every preference: an
/// absent or unhealthy preferred provider falls through.
/// </summary>
public sealed class ActivePreferenceTable
{
    private readonly object _gate = new();
    private readonly Dictionary<CapabilityId, ProviderId> _preferences = new();

    /// <summary>Sets the active provider for <paramref name="capability"/> (replaces any previous value).</summary>
    public void SetPreferred(CapabilityId capability, ProviderId provider)
    {
        lock (_gate)
        {
            _preferences[capability] = provider;
        }
    }

    /// <summary>Removes the active preference (resolution falls back to the configured preference).</summary>
    public void ClearPreferred(CapabilityId capability)
    {
        lock (_gate)
        {
            _preferences.Remove(capability);
        }
    }

    /// <summary>The active preference for <paramref name="capability"/>, or null when none is set.</summary>
    public ProviderId? PreferredFor(CapabilityId capability)
    {
        lock (_gate)
        {
            return _preferences.TryGetValue(capability, out var provider) ? provider : null;
        }
    }

    /// <summary>A snapshot of every active preference (diagnostics).</summary>
    public IReadOnlyDictionary<CapabilityId, ProviderId> Snapshot()
    {
        lock (_gate)
        {
            return _preferences.ToDictionary(entry => entry.Key, entry => entry.Value);
        }
    }
}
