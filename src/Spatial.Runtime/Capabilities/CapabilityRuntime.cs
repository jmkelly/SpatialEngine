using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;

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
/// outcome provenance — and, for long-running capabilities, the job model
/// (ADR-0008): the invocation runs as a tracked job with state, events and
/// cancellation. Also owns the runtime's <see cref="ResourceRegistry"/> and
/// <see cref="JobRegistry"/>. Never references a concrete provider — only
/// <see cref="ICapabilityProvider"/> (architecture tests).
/// </summary>
public sealed class CapabilityRuntime
{
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityResolver _resolver;
    private readonly IPermissionEvaluator _permissions;
    private readonly ResourceRegistry _resources;
    private readonly JobRegistry _jobs;

    public CapabilityRuntime(
        CapabilityRegistry registry,
        CapabilityConfiguration? configuration = null,
        IPermissionEvaluator? permissionEvaluator = null,
        ResourceRegistry? resourceRegistry = null,
        JobRegistry? jobRegistry = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = new CapabilityResolver(registry, configuration ?? CapabilityConfiguration.Empty);
        _permissions = permissionEvaluator ?? new GrantedPermissionsEvaluator();
        _resources = resourceRegistry ?? new ResourceRegistry();
        _jobs = jobRegistry ?? new JobRegistry();
    }

    public CapabilityRegistry Registry => _registry;

    /// <summary>The runtime's resource tracker (opaque handles, leases, disposal, leak detection).</summary>
    public ResourceRegistry Resources => _resources;

    /// <summary>The runtime's job tracker (long-running invocations, ADR-0008).</summary>
    public JobRegistry Jobs => _jobs;

    /// <summary>
    /// Deterministically resolves the provider for <paramref name="capability"/>,
    /// or null when no usable provider exists.
    /// </summary>
    public ResolvedProvider? Resolve(CapabilityId capability, InvocationOptions? options = null) =>
        _resolver.Resolve(capability, options);

    /// <summary>
    /// Invokes <paramref name="invocation"/> end to end: resolve, check
    /// permissions, enforce the deadline through a linked token, and route to
    /// the provider. Long-running capabilities run through the job model — a
    /// job is created, awaited and returned with its job id in the
    /// provenance — so progress and cancellation are uniform (ADR-0008).
    /// Providers never escape with exceptions or null results — those become
    /// structured <see cref="CapabilityErrorKind.ContractViolation"/> or
    /// <see cref="CapabilityErrorKind.ProviderFailure"/> outcomes.
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

        if (DeniedPermissions(invocation, resolved) is { } denied)
        {
            return CapabilityOutcomeFactory.Routed(
                CapabilityResult.Failure(CapabilityError.PermissionDenied(denied)),
                resolved, startedAt, invocation.Deadline);
        }

        if ((resolved.Descriptor.Traits & CapabilityTraits.LongRunning) != 0)
        {
            var job = StartResolvedJob(invocation, options, resolved);
            return await job.WhenOutcomeAsync();
        }

        return await InvokeInlineAsync(invocation, resolved, startedAt);
    }

    /// <summary>
    /// Starts an invocation as a job and returns its handle immediately
    /// (ADR-0008 "clients poll or subscribe through one job API"). Pre-check
    /// failures (unresolvable capability, denied permissions, an
    /// already-expired deadline) return a job already in its terminal state,
    /// so every request still yields a tracked job id.
    /// </summary>
    public CapabilityJob StartJob(CapabilityInvocation invocation, InvocationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        options ??= InvocationOptions.None;

        if (invocation.Deadline is { } due && due <= DateTimeOffset.UtcNow)
        {
            return _jobs.CreateFailed(invocation, CapabilityError.DeadlineExceeded(invocation.Capability), expiredAt: due);
        }

        var resolved = Resolve(invocation.Capability, options);
        if (resolved is null)
        {
            return _jobs.CreateFailed(invocation, _resolver.DescribeUnavailable(invocation.Capability, options));
        }

        if (DeniedPermissions(invocation, resolved) is { } denied)
        {
            return _jobs.CreateFailed(invocation, CapabilityError.PermissionDenied(denied), resolved);
        }

        return StartResolvedJob(invocation, options, resolved);
    }

    private async Task<CapabilityOutcome> InvokeInlineAsync(
        CapabilityInvocation invocation,
        ResolvedProvider resolved,
        DateTimeOffset startedAt)
    {
        using var deadlineCts = CreateDeadlineCts(invocation.Deadline, startedAt, invocation.CancellationToken);
        var effective = invocation with
        {
            CancellationToken = deadlineCts?.Token ?? invocation.CancellationToken,
            Facilities = WithFacilities(invocation, resolved),
        };

        var result = await CapabilityInvoker.InvokeSafelyAsync(resolved, effective);
        result = StreamingContractValidator.Validate(resolved, result, _resources);
        return CapabilityOutcomeFactory.Routed(result, resolved, startedAt, invocation.Deadline);
    }

    private CapabilityJob StartResolvedJob(
        CapabilityInvocation invocation,
        InvocationOptions options,
        ResolvedProvider resolved)
    {
        var job = _jobs.Create(invocation);
        var facilities = CapabilityFacilities.Create(_resources, resolved.Provider.Id, job);
        _ = JobRunner.RunAsync(_resources, facilities, job, resolved, invocation);
        return job;
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

    private IReadOnlyList<Permission>? DeniedPermissions(CapabilityInvocation invocation, ResolvedProvider resolved)
    {
        var missing = _permissions.Missing(invocation.GrantedPermissions, resolved.Descriptor.RequiredPermissions);
        return missing.Count > 0 ? missing : null;
    }

    private ICapabilityFacilities WithFacilities(CapabilityInvocation invocation, ResolvedProvider resolved) =>
        CapabilityFacilities.Create(_resources, resolved.Provider.Id);
}
