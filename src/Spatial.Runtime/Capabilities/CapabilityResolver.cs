using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Deterministic capability resolution (plan §9): explicit caller pin,
/// compatible resource-local provider, configured preferred provider, then
/// the first healthy provider by stable id — plus the actionable diagnostics
/// when nothing can serve. Held by <see cref="CapabilityRuntime"/> so the
/// invocation facade stays thin.
/// </summary>
internal sealed class CapabilityResolver
{
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityConfiguration _configuration;
    private readonly ActivePreferenceTable _active;

    public CapabilityResolver(
        CapabilityRegistry registry,
        CapabilityConfiguration configuration,
        ActivePreferenceTable? active = null)
    {
        _registry = registry;
        _configuration = configuration;
        _active = active ?? new ActivePreferenceTable();
    }

    /// <summary>
    /// Deterministically resolves the provider for <paramref name="capability"/>,
    /// or null when no usable provider exists. The caller's explicit pin is a
    /// hard constraint; resource-local and configured preferences fall
    /// through to the first healthy provider by stable id.
    /// </summary>
    public ResolvedProvider? Resolve(CapabilityId capability, InvocationOptions? options = null)
    {
        var candidates = Candidates(capability);
        options ??= InvocationOptions.None;

        if (options.ExplicitProvider is { } explicitId)
        {
            return ResolveOrNull(candidates, explicitId, ResolutionStep.Explicit);
        }

        return TryLocal(candidates, options)
            ?? TryActivePreferred(candidates, capability)
            ?? TryConfiguredPreferred(candidates, capability)
            ?? TryFirstHealthy(candidates);
    }

    private static ResolvedProvider? ResolveOrNull(List<Candidate> candidates, ProviderId id, ResolutionStep step) =>
        MatchOrNull(candidates, id) is { } match ? ToResolved(match, step) : null;

    private static ResolvedProvider? TryLocal(List<Candidate> candidates, InvocationOptions options)
    {
        if (LocalProvider(options) is not { } localId)
        {
            return null;
        }

        return ResolveOrNull(candidates, localId, ResolutionStep.ResourceLocal);
    }

    private ResolvedProvider? TryActivePreferred(List<Candidate> candidates, CapabilityId capability)
    {
        if (_active.PreferredFor(capability) is not { } active)
        {
            return null;
        }

        return ResolveOrNull(candidates, active, ResolutionStep.ActivePreferred);
    }

    private ResolvedProvider? TryConfiguredPreferred(List<Candidate> candidates, CapabilityId capability)
    {
        if (_configuration.PreferredProviderFor(capability) is not { } configured)
        {
            return null;
        }

        return ResolveOrNull(candidates, configured, ResolutionStep.ConfiguredPreferred);
    }

    private static ResolvedProvider? TryFirstHealthy(List<Candidate> candidates) =>
        candidates.Count == 0 ? null : ToResolved(candidates[0], ResolutionStep.FirstHealthy);

    /// <summary>The actionable error for a failed resolution, naming the reason.</summary>
    public CapabilityError DescribeUnavailable(CapabilityId capability, InvocationOptions options)
    {
        if (options.ExplicitProvider is { } pinned)
        {
            if (_registry.GetProvider(pinned) is not { } pin)
            {
                return CapabilityError.ProviderUnavailable(
                    $"Provider {pinned} is not registered. Registered providers: {ListProviders()}.");
            }

            if (!pin.Descriptors.Any(descriptor => descriptor.Id == capability))
            {
                return CapabilityError.ProviderUnavailable(
                    $"Provider {pinned} cannot serve {capability}; it serves: {ListCapabilities(pin)}. "
                    + $"Call without the explicit provider option, or pin a provider that serves {capability}.");
            }

            return CapabilityError.ProviderUnavailable(
                $"Provider {pinned} serves {capability} but is {pin.Health} and cannot serve requests right now.");
        }

        var serving = _registry.GetProviders(capability);
        if (serving.Count > 0)
        {
            var providers = string.Join(", ", serving.Select(registration => $"{registration.Id} ({registration.Health})"));
            var localHint = LocalHint(options, capability);
            return CapabilityError.ProviderUnavailable(
                $"No usable provider serves {capability}. Registered: {providers}.{localHint} "
                + "Mark one healthy or register another provider.");
        }

        return CapabilityError.CapabilityNotFound(
            $"No provider serves {capability}. Registered capabilities: {ListRegisteredCapabilities()}.");
    }

    private static Candidate? MatchOrNull(List<Candidate> candidates, ProviderId id) =>
        candidates.FirstOrDefault(candidate => candidate.Registration.Id == id);

    /// <summary>The resource-local preference: the owning provider of a passed resource, else the explicit option.</summary>
    private static ProviderId? LocalProvider(InvocationOptions options) =>
        options.Resource is { } resource ? resource.Owner : options.ResourceLocalProvider;

    private static string LocalHint(InvocationOptions options, CapabilityId capability) =>
        options.ResourceLocalProvider is { } local
            ? $" The resource-local provider {local} does not serve {capability}."
            : options.Resource is { } resource
                ? $" The resource owner {resource.Owner} does not serve {capability}."
                : string.Empty;

    private List<Candidate> Candidates(CapabilityId capability)
    {
        var candidates = new List<Candidate>();
        foreach (var registration in _registry.GetProviders(capability))
        {
            if (!registration.CanServe)
            {
                continue;
            }

            var descriptor = registration.Descriptors.First(descriptor => descriptor.Id == capability);
            candidates.Add(new Candidate(registration, descriptor));
        }

        candidates.Sort(static (left, right) => left.Registration.Id.CompareTo(right.Registration.Id));
        return candidates;
    }

    private static string ListCapabilities(ProviderRegistration pin) =>
        string.Join(", ", pin.Descriptors.Select(descriptor => descriptor.Id.ToString()));

    private string ListProviders() =>
        string.Join(", ", _registry.Providers.Select(registration => registration.Id.ToString()));

    private string ListRegisteredCapabilities() =>
        string.Join(", ", _registry.Capabilities.Select(id => id.ToString()));

    private static ResolvedProvider ToResolved(Candidate candidate, ResolutionStep step) =>
        new(candidate.Registration.Provider, candidate.Descriptor, candidate.Registration.Health, step);

    private sealed record Candidate(ProviderRegistration Registration, CapabilityDescriptor Descriptor);
}
