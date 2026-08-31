using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;

namespace Spatial.Runtime.Jobs;

/// <summary>
/// One tracked long-running invocation (ADR-0008): the runtime's concrete
/// <see cref="IJob"/> with a state machine, append-only events, a completion
/// result and the invocation provenance its outcome carries. The job registry
/// creates jobs, the job runner drives the transitions, and clients hold the
/// handle through the <see cref="IJob"/> surface.
/// </summary>
public sealed class CapabilityJob : IJob, IDisposable
{
    private readonly object _gate = new();
    private readonly CapabilityInvocation _invocation;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<CapabilityResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JobEvent> _events = [];
    private JobState _state = JobState.Pending;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private ResolvedProvider? _resolved;
    private DateTimeOffset? _expiredAt;

    internal CapabilityJob(CapabilityInvocation invocation)
    {
        _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        Id = JobId.Create();
        CreatedAt = DateTimeOffset.UtcNow;
        _events.Add(JobEvent.Created());
    }

    public JobId Id { get; }

    /// <summary>
    /// The provider resolution serving this job (the provider, its descriptor
    /// and the deterministic resolution step), or null before it starts and
    /// for pre-check failures that never reached a provider. Hosts surface
    /// this as job provenance (plan §9).
    /// </summary>
    public ResolvedProvider? Resolved
    {
        get
        {
            lock (_gate)
            {
                return _resolved;
            }
        }
    }

    public DateTimeOffset CreatedAt { get; }

    public JobState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_gate)
            {
                return _startedAt;
            }
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get
        {
            lock (_gate)
            {
                return _completedAt;
            }
        }
    }

    public IReadOnlyList<JobEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    // IInvocationContext — the invocation view this job serves.

    public CapabilityId Capability => _invocation.Capability;

    public IReadOnlyDictionary<string, object?> Arguments => _invocation.Arguments;

    public IReadOnlySet<Permission> GrantedPermissions => _invocation.GrantedPermissions;

    public DateTimeOffset? Deadline => _invocation.Deadline;

    public IProgress<ProgressReport>? Progress => _invocation.Progress;

    public CancellationToken CancellationToken => _invocation.CancellationToken;

    public ICapabilityFacilities? Facilities => _invocation.Facilities;

    /// <summary>Waits for the job's terminal <see cref="CapabilityResult"/>.</summary>
    public ValueTask<CapabilityResult> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
        new(_completion.Task.WaitAsync(cancellationToken));

    /// <summary>Requests cancellation of the running invocation (ADR-0008: always cancellable).</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        _cancellation.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases the job's cancellation source. Hosts call this when pruning a
    /// finished job (the job handle and events stay readable afterwards).
    /// </summary>
    public void Dispose() => _cancellation.Dispose();

    /// <summary>The runtime's cancellation token for this job (linked by the runner with the caller token and deadline).</summary>
    internal CancellationToken JobToken => _cancellation.Token;

    /// <summary>The completed outcome with provenance (job id, provider, step, timing).</summary>
    internal async Task<CapabilityOutcome> WhenOutcomeAsync()
    {
        var result = await WaitForCompletionAsync();
        return BuildOutcome(result);
    }

    internal void Start(ResolvedProvider resolved)
    {
        lock (_gate)
        {
            if (!CanTransitionTo(JobState.Running))
            {
                return;
            }

            _resolved = resolved;
            _startedAt = DateTimeOffset.UtcNow;
            _state = JobState.Running;
            _events.Add(JobEvent.Started());
        }
    }

    internal void Complete(CapabilityResult result)
    {
        var (state, jobEvent) = TerminalFor(result);
        Finish(state, jobEvent);
        _completion.TrySetResult(result);
    }

    /// <summary>Finishes a job that failed before the invocation ran (pre-check failures).</summary>
    internal void CompleteFailed(CapabilityError error, ResolvedProvider? resolved, DateTimeOffset? expiredAt = null)
    {
        _resolved = resolved;
        _expiredAt = expiredAt;
        Finish(
            TerminalStateFor(error.Kind),
            error.Kind == CapabilityErrorKind.DeadlineExceeded ? JobEvent.TimedOut(error) : JobEvent.Failed(error));
        _completion.TrySetResult(CapabilityResult.Failure(error));
    }

    /// <summary>Records a resource the job published (stream handles clients can read while the job runs).</summary>
    internal void PublishResource(ResourceHandle handle) => AppendEvent(JobEvent.PublishResource(handle));

    private CapabilityOutcome BuildOutcome(CapabilityResult result)
    {
        var started = StartedAt ?? CreatedAt;
        if (_expiredAt is { } due)
        {
            return CapabilityOutcomeFactory.Expired(_invocation, started, due, Id);
        }

        return _resolved is { } resolved
            ? CapabilityOutcomeFactory.Routed(result, resolved, started, _invocation.Deadline, Id)
            : CapabilityOutcomeFactory.Unavailable(_invocation, FailureError(result), started, Id);
    }

    private void Finish(JobState state, JobEvent jobEvent)
    {
        lock (_gate)
        {
            if (!CanTransitionTo(state))
            {
                return;
            }

            _state = state;
            _completedAt = DateTimeOffset.UtcNow;
            _events.Add(jobEvent);
        }
    }

    internal void AppendEvent(JobEvent jobEvent)
    {
        lock (_gate)
        {
            if (_state is JobState.Pending or JobState.Running)
            {
                _events.Add(jobEvent);
            }
        }
    }

    private bool CanTransitionTo(JobState next) => !IsTerminal(_state) && next != JobState.Pending;

    private static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.TimedOut;

    private static (JobState State, JobEvent Event) TerminalFor(CapabilityResult result)
    {
        if (result is CapabilityFailure failure)
        {
            return failure.Error.Kind switch
            {
                CapabilityErrorKind.DeadlineExceeded => (JobState.TimedOut, JobEvent.TimedOut(failure.Error)),
                CapabilityErrorKind.Cancelled => (JobState.Cancelled, JobEvent.Cancelled()),
                _ => (JobState.Failed, JobEvent.Failed(failure.Error)),
            };
        }

        return (JobState.Completed, JobEvent.Completed());
    }

    private static JobState TerminalStateFor(CapabilityErrorKind kind) =>
        kind == CapabilityErrorKind.DeadlineExceeded ? JobState.TimedOut : JobState.Failed;

    private static CapabilityError FailureError(CapabilityResult result) =>
        result is CapabilityFailure { Error: { } error }
            ? error
            : CapabilityError.ContractViolation("The job completed without a failure result.");
}
