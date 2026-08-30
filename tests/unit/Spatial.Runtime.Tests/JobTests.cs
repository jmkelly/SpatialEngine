using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Phase 4 job model (Epic E "job state and events", ADR-0008): long-running
/// invocations run as jobs with state, append-only events, progress,
/// cancellation and timeout behaviour; clients poll or subscribe through the
/// one job API and every job reaches a terminal state.
/// </summary>
public sealed class JobTests
{
    private static readonly CapabilityId Sleep = ExampleFeatureProvider.SleepCapability;

    [Fact]
    public async Task StartJob_completes_with_events_in_order()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(SleepInvocation(80));

        Assert.True(job.State is JobState.Pending or JobState.Running);

        var result = await job.WaitForCompletionAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(80L, Assert.IsType<CapabilitySuccess>(result).Value);
        Assert.Equal(JobState.Completed, job.State);
        Assert.NotNull(job.CompletedAt);
        Assert.True(job.CompletedAt >= job.StartedAt);

        var events = job.Events.ToArray();
        Assert.Equal(JobEventKind.Created, events[0].Kind);
        Assert.Contains(events, jobEvent => jobEvent.Kind == JobEventKind.Started);
        Assert.Contains(events, jobEvent => jobEvent.Kind == JobEventKind.Completed);
        Assert.Contains(events, jobEvent => jobEvent.Kind == JobEventKind.Progress);
        var startedIndex = Array.FindIndex(events, jobEvent => jobEvent.Kind == JobEventKind.Started);
        var completedIndex = Array.FindIndex(events, jobEvent => jobEvent.Kind == JobEventKind.Completed);
        Assert.True(startedIndex >= 0 && startedIndex < completedIndex);
    }

    [Fact]
    public async Task Jobs_are_tracked_by_id_and_can_be_forgotten()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(SleepInvocation(20));

        Assert.True(host.Jobs.TryGet(job.Id, out var tracked));
        Assert.Same(job, tracked);
        Assert.Equal(1, host.Jobs.Count);

        await job.WaitForCompletionAsync();
        Assert.True(host.Jobs.Forget(job.Id));
        Assert.False(host.Jobs.TryGet(job.Id, out _));
    }

    [Fact]
    public async Task A_failing_job_records_the_error_event_and_state()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [Sleep],
                handler: _ => new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ProviderFailure("boom")))));

        var job = runtime.StartJob(CapabilityInvocation.Create(Sleep, new Dictionary<string, object?>()));

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.ProviderFailure, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Equal(JobState.Failed, job.State);
        var failedEvent = Assert.Single(job.Events, jobEvent => jobEvent.Kind == JobEventKind.Failed);
        Assert.Equal("provider.failure", failedEvent.Error?.Code);
    }

    [Fact]
    public async Task A_throwing_provider_job_fails_structured()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider("alpha", 1, [Sleep], handler: _ => throw new InvalidOperationException("kaboom")));

        var job = runtime.StartJob(CapabilityInvocation.Create(Sleep, new Dictionary<string, object?>()));

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.ProviderFailure, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Contains("kaboom", Assert.IsType<CapabilityFailure>(result).Error.Message);
        Assert.Equal(JobState.Failed, job.State);
    }

    [Fact]
    public async Task Cancelling_a_running_job_ends_it_cancelled()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(SleepInvocation(5000));

        await WaitForStateAsync(job, JobState.Running);
        await job.CancelAsync();

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Contains(job.Events, jobEvent => jobEvent.Kind == JobEventKind.Cancelled);
    }

    [Fact]
    public async Task A_passed_deadline_times_the_job_out()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(
            SleepInvocation(5000) with { Deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(40) });

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Equal(JobState.TimedOut, job.State);
        Assert.Contains(job.Events, jobEvent => jobEvent.Kind == JobEventKind.TimedOut);
        Assert.DoesNotContain(job.Events, jobEvent => jobEvent.Kind == JobEventKind.Completed);
    }

    [Fact]
    public async Task StartJob_precheck_failures_yield_terminal_jobs()
    {
        var host = InMemoryComponentHost.Create();

        var unknown = host.Runtime.StartJob(
            CapabilityInvocation.Create(CapabilityId.Parse("spatial.geometry.buffer@1"), new Dictionary<string, object?>()));
        Assert.Equal(JobState.Failed, unknown.State);
        Assert.Equal(CapabilityErrorKind.CapabilityNotFound, Assert.IsType<CapabilityFailure>(await unknown.WaitForCompletionAsync()).Error.Kind);

        var denied = host.Runtime.StartJob(
            CapabilityInvocation.Create(ExampleFeatureProvider.EnvelopeCapability, new Dictionary<string, object?>()));
        Assert.Equal(JobState.Failed, denied.State);
        Assert.Equal(CapabilityErrorKind.PermissionDenied, Assert.IsType<CapabilityFailure>(await denied.WaitForCompletionAsync()).Error.Kind);

        var expired = host.Runtime.StartJob(
            SleepInvocation(10) with { Deadline = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1) });
        Assert.Equal(JobState.TimedOut, expired.State);
        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, Assert.IsType<CapabilityFailure>(await expired.WaitForCompletionAsync()).Error.Kind);
    }

    [Fact]
    public async Task Long_running_invocations_route_through_jobs()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(SleepInvocation(60));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, host.Jobs.Count);
        Assert.NotNull(outcome.Provenance.JobId);
        Assert.Equal(outcome.Provenance.JobId, host.Jobs.Jobs[0].Id);
    }

    [Fact]
    public async Task Small_invocations_run_inline_without_a_job()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(
                ExampleFeatureProvider.CountCapability,
                new Dictionary<string, object?> { ["batch"] = FixtureBatches.Points((0, 0), (1, 1)) }));

        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.Provenance.JobId);
        Assert.Equal(0, host.Jobs.Count);
    }

    [Fact]
    public async Task Job_outcome_carries_provider_step_and_job_id()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [Sleep],
                traits: CapabilityTraits.LongRunning | CapabilityTraits.Cancellable));

        var outcome = await runtime.InvokeAsync(SleepInvocation(30));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderId.Parse("alpha@1"), outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
        Assert.NotNull(outcome.Provenance.JobId);
        var job = Assert.Single(runtime.Jobs.Jobs);
        Assert.Equal(outcome.Provenance.JobId, job.Id);
        Assert.Equal(Sleep, job.Capability);
    }

    [Fact]
    public async Task Job_exposes_the_invocation_context_surface()
    {
        var host = InMemoryComponentHost.Create();
        using var cts = new CancellationTokenSource();
        var arguments = new Dictionary<string, object?> { ["milliseconds"] = 30L };
        var job = host.Runtime.StartJob(
            CapabilityInvocation.Create(Sleep, arguments)
            with
            {
                GrantedPermissions = new HashSet<Permission> { ExampleFeatureProvider.ReadPermission },
                CancellationToken = cts.Token,
            });

        Assert.Equal(Sleep, job.Capability);
        Assert.Equal(arguments, job.Arguments);
        Assert.Contains(ExampleFeatureProvider.ReadPermission, job.GrantedPermissions);
        Assert.Equal(cts.Token, job.CancellationToken);
        Assert.Null(job.Facilities);

        var result = await job.WaitForCompletionAsync();
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CancelAsync_after_completion_is_harmless()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(SleepInvocation(10));
        Assert.True((await job.WaitForCompletionAsync()).IsSuccess);

        await job.CancelAsync();
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public async Task Progress_reports_reach_the_caller_sink_and_the_events()
    {
        var host = InMemoryComponentHost.Create();
        var reports = new List<ProgressReport>();
        var job = host.Runtime.StartJob(SleepInvocation(80) with { Progress = new RecordingProgress(reports) });

        var result = await job.WaitForCompletionAsync();
        Assert.True(result.IsSuccess);

        Assert.NotEmpty(reports);
        Assert.Equal(reports.Count, job.Events.Count(jobEvent => jobEvent.Kind == JobEventKind.Progress));
        Assert.All(reports, report => Assert.InRange(report.Fraction!.Value, 0, 1));
    }

    [Fact]
    public void Job_event_factories_carry_the_payloads()
    {
        var progress = JobEvent.ReportProgress(ProgressReport.Create(0.5, "half"));
        Assert.Equal(JobEventKind.Progress, progress.Kind);
        Assert.Equal(0.5, progress.Progress?.Fraction);

        var error = JobEvent.Failed(CapabilityError.InvalidArguments("bad"));
        Assert.Equal(JobEventKind.Failed, error.Kind);
        Assert.Equal("invalid.arguments", error.Error?.Code);

        var diagnostic = JobEvent.Diagnostic("note");
        Assert.Equal(JobEventKind.Note, diagnostic.Kind);
        Assert.Equal("note", diagnostic.Note);

        Assert.Equal(JobEventKind.Created, JobEvent.Created().Kind);
        Assert.Equal(JobEventKind.Started, JobEvent.Started().Kind);
        Assert.Equal(JobEventKind.Completed, JobEvent.Completed().Kind);
        Assert.Equal(JobEventKind.Cancelled, JobEvent.Cancelled().Kind);
        Assert.Equal(JobEventKind.TimedOut, JobEvent.TimedOut(CapabilityError.Cancelled(Sleep)).Kind);
    }

    [Fact]
    public async Task Cancelling_a_job_with_a_deadline_is_a_cancel_not_a_timeout()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(
            SleepInvocation(5000) with { Deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10) });

        await WaitForStateAsync(job, JobState.Running);
        await job.CancelAsync();

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task A_caller_token_cancel_with_a_deadline_is_a_cancel_not_a_timeout()
    {
        var host = InMemoryComponentHost.Create();
        using var cts = new CancellationTokenSource();
        var job = host.Runtime.StartJob(
            SleepInvocation(5000)
            with
            {
                Deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10),
                CancellationToken = cts.Token,
            });

        await WaitForStateAsync(job, JobState.Running);
        await cts.CancelAsync();

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, Assert.IsType<CapabilityFailure>(result).Error.Kind);
    }

    private static CapabilityInvocation SleepInvocation(long milliseconds) =>
        CapabilityInvocation.Create(Sleep, new Dictionary<string, object?> { ["milliseconds"] = milliseconds });

    private static async Task WaitForStateAsync(CapabilityJob job, JobState state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (job.State != state && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.Equal(state, job.State);
    }

    private static (CapabilityRegistry Registry, CapabilityRuntime Runtime) CreateRuntime(params ICapabilityProvider[] providers)
    {
        var registry = new CapabilityRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider);
        }

        return (registry, new CapabilityRuntime(registry));
    }
}
