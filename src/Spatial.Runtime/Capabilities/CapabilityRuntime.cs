using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The default permission policy (plan §6.1 "permission evaluation"): a
/// required permission is granted only when it is a member of the
/// invocation's granted set. Hosts may supply another
/// <see cref="IPermissionEvaluator"/> to the runtime.
/// </summary>
internal sealed class GrantedPermissionsEvaluator : IPermissionEvaluator
{
    public IReadOnlyList<Permission> Missing(
        IReadOnlySet<Permission> granted,
        IReadOnlyList<Permission> required) =>
        required.Where(permission => !granted.Contains(permission)).ToArray();
}

/// <summary>
/// Routes invocations to providers for a registered capability: deterministic
/// resolution (delegated to <see cref="CapabilityResolver"/>), permission
/// checks, deadline and cancellation enforcement, structured errors and
/// outcome provenance. Never references a concrete provider — only
/// <see cref="ICapabilityProvider"/> (architecture tests).
/// </summary>
public sealed class CapabilityRuntime
{
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityResolver _resolver;
    private readonly IPermissionEvaluator _permissions;

    public CapabilityRuntime(
        CapabilityRegistry registry,
        CapabilityConfiguration? configuration = null,
        IPermissionEvaluator? permissionEvaluator = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = new CapabilityResolver(registry, configuration ?? CapabilityConfiguration.Empty);
        _permissions = permissionEvaluator ?? new GrantedPermissionsEvaluator();
    }

    public CapabilityRegistry Registry => _registry;

    /// <summary>
    /// Deterministically resolves the provider for <paramref name="capability"/>,
    /// or null when no usable provider exists.
    /// </summary>
    public ResolvedProvider? Resolve(CapabilityId capability, InvocationOptions? options = null) =>
        _resolver.Resolve(capability, options);

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
            return CapabilityOutcomeFactory.Expired(invocation, startedAt, due);
        }

        var resolved = Resolve(invocation.Capability, options);
        if (resolved is null)
        {
            return UnavailableOutcome(invocation, options, startedAt);
        }

        var missing = _permissions.Missing(invocation.GrantedPermissions, resolved.Descriptor.RequiredPermissions);
        if (missing.Count > 0)
        {
            return CapabilityOutcomeFactory.Routed(
                CapabilityResult.Failure(CapabilityError.PermissionDenied(missing)),
                resolved, startedAt, invocation.Deadline);
        }

        using var deadlineCts = CreateDeadlineCts(invocation.Deadline, startedAt, invocation.CancellationToken);
        var effective = deadlineCts is null
            ? invocation
            : invocation with { CancellationToken = deadlineCts.Token };

        var result = await CapabilityInvoker.InvokeSafelyAsync(resolved, effective);
        return CapabilityOutcomeFactory.Routed(result, resolved, startedAt, invocation.Deadline);
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

    private CapabilityOutcome UnavailableOutcome(
        CapabilityInvocation invocation,
        InvocationOptions options,
        DateTimeOffset startedAt)
    {
        var error = _resolver.DescribeUnavailable(invocation.Capability, options);
        return CapabilityOutcomeFactory.Unavailable(invocation, error, startedAt);
    }
}
