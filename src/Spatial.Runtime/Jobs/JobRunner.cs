using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Resources;
using Spatial.Runtime.Streams;

namespace Spatial.Runtime.Jobs;

/// <summary>
/// Drives one job's invocation (ADR-0008): links the caller token and the
/// job's own cancellation with the deadline, attaches the runtime facilities
/// and a progress relay, runs the provider through the guarded invoker,
/// enforces the streaming contract and completes the job in its terminal
/// state. Every path reaches a terminal state — the runner never lets a job
/// hang.
/// </summary>
internal static class JobRunner
{
    public static async Task RunAsync(
        ResourceRegistry resources,
        ICapabilityFacilities facilities,
        CapabilityJob job,
        ResolvedProvider resolved,
        CapabilityInvocation invocation)
    {
        try
        {
            job.Start(resolved);
            using var deadlineCts = CreateDeadlineCts(job, invocation);
            var effective = invocation with
            {
                CancellationToken = deadlineCts.Token,
                Facilities = facilities,
                Progress = new JobProgressRelay(
                    invocation.Progress,
                    report => job.AppendEvent(JobEvent.ReportProgress(report))),
            };

            var result = await CapabilityInvoker.InvokeSafelyAsync(resolved, effective);
            result = StreamingContractValidator.Validate(resolved, result, resources);
            result = AttributeDeadlineCancellation(result, job, invocation);
            job.Complete(result);
        }
        catch (Exception exception)
        {
            job.CompleteFailed(
                CapabilityError.ProviderFailure(
                    $"The job {job.Id} failed while running {invocation.Capability}: {exception.Message}"),
                resolved);
        }
    }

    /// <summary>
    /// Attributes an observed cancellation to the deadline when neither the
    /// caller nor the job itself requested it. The deadline timer can fire a
    /// few milliseconds early (timer coalescing), so a wall-clock comparison
    /// alone mislabels a deadline-caused cancellation as a caller cancel;
    /// when the caller token and the job token are both quiet, the linked
    /// cancellation can only have come from the deadline.
    /// </summary>
    private static CapabilityResult AttributeDeadlineCancellation(
        CapabilityResult result,
        CapabilityJob job,
        CapabilityInvocation invocation)
    {
        if (result is CapabilityFailure { Error.Kind: CapabilityErrorKind.Cancelled }
            && invocation.Deadline is not null
            && !invocation.CancellationToken.IsCancellationRequested
            && !job.JobToken.IsCancellationRequested)
        {
            return CapabilityResult.Failure(CapabilityError.DeadlineExceeded(invocation.Capability));
        }

        return result;
    }

    private static CancellationTokenSource CreateDeadlineCts(CapabilityJob job, CapabilityInvocation invocation)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(job.JobToken, invocation.CancellationToken);
        if (invocation.Deadline is { } due)
        {
            var remaining = due - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                cts.Cancel();
            }
            else
            {
                cts.CancelAfter(remaining);
            }
        }

        return cts;
    }

    /// <summary>
    /// Forwards provider progress observations to the caller's sink AND
    /// records a job progress event — one observation, two consumers.
    /// </summary>
    private sealed class JobProgressRelay : IProgress<ProgressReport>
    {
        private readonly IProgress<ProgressReport>? _inner;
        private readonly Action<ProgressReport> _record;

        public JobProgressRelay(IProgress<ProgressReport>? inner, Action<ProgressReport> record)
        {
            _inner = inner;
            _record = record;
        }

        public void Report(ProgressReport value)
        {
            _record(value);
            _inner?.Report(value);
        }
    }
}
