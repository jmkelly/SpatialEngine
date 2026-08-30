using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Routes invocations to providers for a registered capability — the public
/// invocation facade: deterministic resolution (delegated to
/// <see cref="CapabilityResolver"/>), the shared permission gate, deadline
/// enforcement, structured outcomes and provenance. Long-running
/// capabilities run as tracked jobs with state, events and cancellation
/// (ADR-0008); the mechanical details live in
/// <see cref="InlineInvocation"/>, <see cref="JobLauncher"/> and
/// <see cref="PermissionGate"/> so this type stays a thin composition root.
/// The facade owns the runtime's <see cref="ResourceRegistry"/> and
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
        _permissions = permissionEvaluator ?? PermissionDefaults.Membership;
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
            return InlineInvocation.Expired(invocation, startedAt, due);
        }

        var resolved = Resolve(invocation.Capability, options);
        if (resolved is null)
        {
            return InlineInvocation.Unavailable(
                invocation, _resolver.DescribeUnavailable(invocation.Capability, options), startedAt);
        }

        if (PermissionGate.Denied(_permissions, invocation, resolved) is { } denied)
        {
            return InlineInvocation.Denied(invocation, resolved, denied, startedAt);
        }

        if (JobLauncher.IsLongRunning(resolved))
        {
            return await JobLauncher.Launch(_jobs, _resources, invocation, resolved).WhenOutcomeAsync();
        }

        return await InlineInvocation.RunAsync(_resources, invocation, resolved, startedAt);
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

        if (PermissionGate.Denied(_permissions, invocation, resolved) is { } denied)
        {
            return _jobs.CreateFailed(invocation, denied, resolved);
        }

        return JobLauncher.Launch(_jobs, _resources, invocation, resolved);
    }
}
