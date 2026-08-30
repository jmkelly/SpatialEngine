using System.Collections.Immutable;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Immutable configured provider preferences: which provider should serve a
/// capability when the caller did not pin a provider (plan §9 step 3).
/// Preferences are soft — an absent or unhealthy preferred provider falls
/// through to the first healthy provider by stable id.
/// </summary>
public sealed class CapabilityConfiguration
{
    public static CapabilityConfiguration Empty { get; } = new(ImmutableDictionary<CapabilityId, ProviderId>.Empty);

    private readonly IReadOnlyDictionary<CapabilityId, ProviderId> _preferences;

    private CapabilityConfiguration(IReadOnlyDictionary<CapabilityId, ProviderId> preferences)
    {
        _preferences = preferences;
    }

    /// <summary>A configuration with a single preference.</summary>
    public static CapabilityConfiguration WithPreference(CapabilityId capability, ProviderId provider) =>
        new(ImmutableDictionary<CapabilityId, ProviderId>.Empty.Add(capability, provider));

    /// <summary>A configuration from an existing mapping; the mapping is defensively copied.</summary>
    public static CapabilityConfiguration FromPreferences(IReadOnlyDictionary<CapabilityId, ProviderId> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new CapabilityConfiguration(preferences.ToImmutableDictionary());
    }

    /// <summary>The preferred provider for <paramref name="capability"/>, or null when none is configured.</summary>
    public ProviderId? PreferredProviderFor(CapabilityId capability) =>
        _preferences.TryGetValue(capability, out var provider) ? provider : null;

    public IReadOnlyDictionary<CapabilityId, ProviderId> Preferences => _preferences;
}