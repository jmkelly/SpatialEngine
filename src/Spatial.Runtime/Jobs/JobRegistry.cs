using System.Diagnostics.CodeAnalysis;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.Runtime.Capabilities;

namespace Spatial.Runtime.Jobs;

/// <summary>
/// Tracks jobs by id (ADR-0008 "tracked by a job id"): creates jobs for
/// long-running invocations, keeps them queryable for polling or
/// subscription and exposes the tracked set for diagnostics. Thread-safe.
/// </summary>
public sealed class JobRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<JobId, CapabilityJob> _jobs = new();

    /// <summary>Creates and registers a pending job (state Pending, event Created).</summary>
    public CapabilityJob Create(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        lock (_gate)
        {
            return Register(new CapabilityJob(invocation));
        }
    }

    /// <summary>
    /// Creates and registers a job that already reached its terminal state —
    /// the pre-check failures (unresolvable capability, denied permissions,
    /// an already-expired deadline) so every request still yields a job id.
    /// </summary>
    public CapabilityJob CreateFailed(
        CapabilityInvocation invocation,
        CapabilityError error,
        ResolvedProvider? resolved = null,
        DateTimeOffset? expiredAt = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            var job = new CapabilityJob(invocation);
            job.CompleteFailed(error, resolved, expiredAt);
            return Register(job);
        }
    }

    /// <summary>Looks up a tracked job by its opaque id.</summary>
    public bool TryGet(JobId id, [NotNullWhen(true)] out CapabilityJob? job)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out job);
        }
    }

    /// <summary>Removes a job from the tracker (hosts prune completed jobs; the job handle keeps working).</summary>
    public bool Forget(JobId id)
    {
        lock (_gate)
        {
            return _jobs.Remove(id);
        }
    }

    /// <summary>All tracked jobs, in creation order.</summary>
    public IReadOnlyList<CapabilityJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count;
            }
        }
    }

    private CapabilityJob Register(CapabilityJob job)
    {
        _jobs.Add(job.Id, job);
        return job;
    }
}
