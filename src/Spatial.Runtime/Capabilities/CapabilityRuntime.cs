using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Routes invocations to providers for a registered capability, following the
/// deterministic resolution order of the plan (§9): explicit caller pin,
/// compatible resource-local provider, configured preferred provider, then
/// the first healthy provider by stable id. Enforces permissions, deadlines,
/// cancellation and structured errors, and records provenance for every
/// outcome. Never references a concrete provider — only
/// <see cref="ICapabilityProvider"/> (architecture tests).
/// </summary>
public sealed class CapabilityRuntime
{
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityConfiguration _configuration;

    public CapabilityRuntime(CapabilityRegistry registry, CapabilityConfiguration? configuration = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configuration = configuration ?? CapabilityConfiguration.Empty;
    }

    public CapabilityRegistry Registry => _registry;

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
            var match = candidates.FirstOrDefault(candidate => candidate.Registration.Id == explicitId);
            return match is null ? null : ToResolved(match, ResolutionStep.Explicit);
        }

        if (options.ResourceLocalProvider is { } localId)
        {
            var match = candidates.FirstOrDefault(candidate => candidate.Registration.Id == localId);
            if (match is not null)
            {
                return ToResolved(match, ResolutionStep.ResourceLocal);
            }
        }

        if (_configuration.PreferredProviderFor(capability) is { } preferred)
        {
            var match = candidates.FirstOrDefault(candidate => candidate.Registration.Id == preferred);
            if (match is not null)
            {
                return ToResolved(match, ResolutionStep.ConfiguredPreferred);
            }
        }

        return candidates.Count == 0 ? null : ToResolved(candidates[0], ResolutionStep.FirstHealthy);
    }

    /// <summary>
    /// Invokes <paramref name="invocation"/> end to end: resolve, check
    /// permissions, enforce the deadline through a linked token, and route to
    /// the provider. Providers never escape with exceptions or null results —
    /// those become structured <see cref="CapabilityErrorKind.ContractViolation"/>
    /// or <see cref="CapabilityErrorKind.ProviderFailure"/> outcomes.
    /// </summary>
    public async Task<CapabilityOutcome> InvokeAsync(CapabilityInvocation invocation, InvocationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        options ??= InvocationOptions.None;
        var startedAt = DateTimeOffset.UtcNow;

        if (invocation.Deadline is { } due && due <= startedAt)
        {
            return ExpiredOutcome(invocation, startedAt, due);
        }

        var resolved = Resolve(invocation.Capability, options);
        if (resolved is null)
        {
            return UnavailableOutcome(invocation, options, startedAt);
        }

        if (HasMissingPermissions(resolved.Descriptor, invocation.GrantedPermissions, out var missing))
        {
            return Outcome(
                CapabilityResult.Failure(CapabilityError.PermissionDenied(missing)),
                resolved, startedAt, invocation.Deadline);
        }

        using var deadlineCts = CreateDeadlineCts(invocation.Deadline, startedAt, invocation.CancellationToken);
        var effective = deadlineCts is null
            ? invocation
            : invocation with { CancellationToken = deadlineCts.Token };

        var result = await InvokeSafelyAsync(resolved, effective, startedAt);
        return Outcome(result, resolved, startedAt, invocation.Deadline);
    }

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

    private static async Task<CapabilityResult> InvokeSafelyAsync(ResolvedProvider resolved, CapabilityInvocation invocation, DateTimeOffset startedAt)
    {
        try
        {
            var result = await resolved.Provider.InvokeAsync(invocation);
            if (result is CapabilityFailure { Error: null })
            {
                return CapabilityResult.Failure(CapabilityError.ContractViolation(
                    $"Provider {resolved.Provider.Id} returned a failure without an error for {invocation.Capability}."));
            }

            return result ?? CapabilityResult.Failure(CapabilityError.ContractViolation(
                $"Provider {resolved.Provider.Id} returned no result for {invocation.Capability}; "
                + "providers must return CapabilitySuccess or CapabilityFailure."));
        }
        catch (OperationCanceledException)
        {
            return invocation.Deadline is { } due && DateTimeOffset.UtcNow >= due
                ? CapabilityResult.Failure(CapabilityError.DeadlineExceeded(invocation.Capability))
                : CapabilityResult.Failure(CapabilityError.Cancelled(invocation.Capability));
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(CapabilityError.ProviderFailure(
                $"Provider {resolved.Provider.Id} threw {exception.GetType().Name} while serving {invocation.Capability}: {exception.Message}"));
        }
    }

    private static bool HasMissingPermissions(
        CapabilityDescriptor descriptor,
        IReadOnlySet<Permission> granted,
        out Permission[] missing)
    {
        missing = descriptor.RequiredPermissions
            .Where(required => !granted.Contains(required))
            .ToArray();
        return missing.Length > 0;
    }

    private static CancellationTokenSource? CreateDeadlineCts(
        DateTimeOffset? deadline,
        DateTimeOffset startedAt,
        CancellationToken callerToken)
    {
        if (deadline is not { } due || due <= startedAt)
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(due - startedAt);
        return cts;
    }

    private static CapabilityOutcome Outcome(
        CapabilityResult result,
        ResolvedProvider resolved,
        DateTimeOffset startedAt,
        DateTimeOffset? deadline) =>
        new(result, new InvocationProvenance(
            resolved.Descriptor.Id, startedAt, DateTimeOffset.UtcNow - startedAt, resolved.Provider.Id, resolved.Step, deadline));

    private static CapabilityOutcome ExpiredOutcome(CapabilityInvocation invocation, DateTimeOffset startedAt, DateTimeOffset due) =>
        new(
            CapabilityResult.Failure(CapabilityError.DeadlineExceeded(invocation.Capability)),
            new InvocationProvenance(invocation.Capability, startedAt, DateTimeOffset.UtcNow - startedAt, null, null, due));

    private CapabilityOutcome UnavailableOutcome(
        CapabilityInvocation invocation,
        InvocationOptions options,
        DateTimeOffset startedAt)
    {
        var error = DescribeUnavailable(invocation.Capability, options);
        var provenance = new InvocationProvenance(
            invocation.Capability, startedAt, DateTimeOffset.UtcNow - startedAt, null, null, invocation.Deadline);
        return new CapabilityOutcome(CapabilityResult.Failure(error), provenance);
    }

    private CapabilityError DescribeUnavailable(CapabilityId capability, InvocationOptions options)
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
            var localHint = options.ResourceLocalProvider is { } local
                ? $" The resource-local provider {local} does not serve {capability}."
                : string.Empty;
            return CapabilityError.ProviderUnavailable(
                $"No usable provider serves {capability}. Registered: {providers}.{localHint} "
                + "Mark one healthy or register another provider.");
        }

        return CapabilityError.CapabilityNotFound(
            $"No provider serves {capability}. Registered capabilities: {ListRegisteredCapabilities()}.");
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