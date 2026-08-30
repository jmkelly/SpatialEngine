using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The capability registry: providers register with their descriptors, and
/// the runtime resolves invocations through it. Registration validates the
/// provider's descriptors (plan §9, ADR-0007/0008). Not thread-safe —
/// register providers while wiring the host, before serving requests.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<ProviderId, ProviderRegistration> _providers = new();
    private readonly Dictionary<CapabilityId, List<ProviderId>> _byCapability = new();

    /// <summary>
    /// Registers a provider (healthy) and its capability descriptors.
    /// Throws <see cref="CapabilityRegistrationException"/> when the provider
    /// id is already registered or a descriptor violates a contract rule.
    /// </summary>
    public void Register(ICapabilityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (_providers.ContainsKey(provider.Id))
        {
            throw new CapabilityRegistrationException(
                $"Provider {provider.Id} is already registered; unregister it before registering a replacement.");
        }

        var descriptors = provider.Descriptors.ToArray();
        ValidateProvider(provider.Id, descriptors);

        _providers.Add(provider.Id, new ProviderRegistration(provider.Id, provider, descriptors, ProviderHealth.Healthy));
        foreach (var descriptor in descriptors)
        {
            if (!_byCapability.TryGetValue(descriptor.Id, out var providerIds))
            {
                providerIds = [];
                _byCapability.Add(descriptor.Id, providerIds);
            }

            providerIds.Add(provider.Id);
        }
    }

    /// <summary>Removes a provider and its descriptors. Returns false when the id is not registered.</summary>
    public bool Unregister(ProviderId id)
    {
        if (!_providers.Remove(id, out var removed))
        {
            return false;
        }

        foreach (var descriptor in removed.Descriptors)
        {
            if (_byCapability.TryGetValue(descriptor.Id, out var providerIds))
            {
                providerIds.Remove(id);
                if (providerIds.Count == 0)
                {
                    _byCapability.Remove(descriptor.Id);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Updates a provider's health. Returns false when the id is not
    /// registered; throws when the health value is unknown.
    /// </summary>
    public bool SetHealth(ProviderId id, ProviderHealth health)
    {
        if (!Enum.IsDefined(health))
        {
            throw new ArgumentOutOfRangeException(nameof(health), health, $"Unknown provider health {(int)health}.");
        }

        if (!_providers.TryGetValue(id, out var registration))
        {
            return false;
        }

        _providers[id] = registration.WithHealth(health);
        return true;
    }

    public ProviderRegistration? GetProvider(ProviderId id) => _providers.GetValueOrDefault(id);

    /// <summary>The providers that serve <paramref name="capability"/>, in registration order.</summary>
    public IReadOnlyList<ProviderRegistration> GetProviders(CapabilityId capability) =>
        _byCapability.TryGetValue(capability, out var providerIds)
            ? providerIds.Select(id => _providers[id]).ToArray()
            : [];

    /// <summary>All registered capability ids, ordered (used for diagnostics).</summary>
    public IReadOnlyList<CapabilityId> Capabilities => _byCapability.Keys.Order().ToArray();

    /// <summary>All registered providers, ordered by stable provider id (used for diagnostics).</summary>
    public IReadOnlyList<ProviderRegistration> Providers => _providers.Values.OrderBy(registration => registration.Id).ToArray();

    public int ProviderCount => _providers.Count;

    private static void ValidateProvider(ProviderId providerId, CapabilityDescriptor[] descriptors)
    {
        if (descriptors.Length == 0)
        {
            throw new CapabilityRegistrationException($"Provider {providerId} declares no capabilities.");
        }

        var seen = new HashSet<CapabilityId>();
        foreach (var descriptor in descriptors)
        {
            if (descriptor is null)
            {
                throw new CapabilityRegistrationException($"Provider {providerId} declares a null capability descriptor.");
            }

            ValidateDescriptor(providerId, descriptor);
            if (!seen.Add(descriptor.Id))
            {
                throw new CapabilityRegistrationException(
                    $"Provider {providerId} declares {descriptor.Id} more than once; a provider serves each capability once.");
            }
        }
    }

    private static void ValidateDescriptor(ProviderId providerId, CapabilityDescriptor descriptor)
    {
        var id = descriptor.Id;
        if (string.IsNullOrWhiteSpace(descriptor.Purpose))
        {
            throw new CapabilityRegistrationException($"Provider {providerId}: capability {id} must declare a purpose (plan §9).");
        }

        if (descriptor.Input is null || string.IsNullOrWhiteSpace(descriptor.Input.Name))
        {
            throw new CapabilityRegistrationException($"Provider {providerId}: capability {id} must declare a named input schema.");
        }

        if (descriptor.Output is null || string.IsNullOrWhiteSpace(descriptor.Output.Name))
        {
            throw new CapabilityRegistrationException($"Provider {providerId}: capability {id} must declare a named output schema.");
        }

        if (descriptor.Errors is null || descriptor.Errors.Count == 0)
        {
            throw new CapabilityRegistrationException(
                $"Provider {providerId}: capability {id} must declare at least one error variant (plan §9).");
        }

        if (descriptor.RequiredPermissions is null)
        {
            throw new CapabilityRegistrationException(
                $"Provider {providerId}: capability {id} must declare its required permissions (possibly none).");
        }

        if ((descriptor.Traits & CapabilityTraits.LongRunning) != 0
            && (descriptor.Traits & CapabilityTraits.Cancellable) == 0)
        {
            throw new CapabilityRegistrationException(
                $"Provider {providerId}: capability {id} is long-running but not cancellable; "
                + "long-running capabilities are always cancellable (ADR-0008).");
        }
    }
}