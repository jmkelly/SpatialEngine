using Spatial.Plugin.Fixtures;
using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// Phase 5 supervision (plan §10.4 lifecycle, Epic F): activation, health,
/// side-by-side routing, draining, rollback and crash restart over *real*
/// worker processes — plus resources, streams, jobs and cancellation across
/// the process boundary and the runtime's active-preference routing.
/// </summary>
public sealed class SupervisorLifecycleTests : IAsyncDisposable
{
    private readonly List<SupervisorRig> _rigs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discovery_finds_packages_and_skips_invalid_directories()
    {
        await using var rig = CreateRig();
        var junk = Path.Combine(Path.GetDirectoryName(rig.V1Package)!, "not-a-package");
        Directory.CreateDirectory(junk);

        var packages = rig.Supervisor.Discover(Path.GetDirectoryName(rig.V1Package)!);

        Assert.Equal(2, packages.Count);
        Assert.Equal(["fixture@1", "fixture@2"], packages.Select(package => package.Manifest.Id).ToArray());
    }

    [Fact]
    public async Task Activation_registers_and_serves_a_real_worker()
    {
        await using var rig = CreateRig();

        var worker = await rig.Supervisor.ActivateAsync(rig.V1Package);

        Assert.Equal(WorkerState.Active, worker.State);
        Assert.Single(rig.Registry.Providers, provider => provider.Id == worker.ProviderId);
        var outcome = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.True(outcome.IsSuccess);
        Assert.Equal("fixture@1", outcome.TryGetValue(out var value) ? value : null);
        Assert.True(await rig.Supervisor.HealthCheckOnceAsync(worker.Id));
    }

    [Fact]
    public async Task A_duplicate_activation_is_rejected_with_an_actionable_error()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);

        var exception = await Assert.ThrowsAsync<WorkerActivationException>(
            () => rig.Supervisor.ActivateAsync(rig.V1Package));
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public async Task Mint_returns_a_runtime_owned_handle()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);

        var outcome = await rig.InvokeAsync(FixtureProviderV1.MintCapability);
        var handle = Assert.IsType<ResourceHandle>(outcome.TryGetValue(out var value) ? value : null);
        Assert.Equal(FixtureProviderV1.ProviderIdentifier, handle.Owner);
        Assert.Equal(ResourceState.Open, rig.Runtime.Resources.GetState(handle));

        var leaked = await rig.Runtime.Resources.DisposeOwnerAsync(FixtureProviderV1.ProviderIdentifier);
        Assert.Equal(1, leaked);
        Assert.Equal(ResourceState.Closed, rig.Runtime.Resources.GetState(handle));
    }

    [Fact]
    public async Task Job_cancellation_and_deadline_cross_the_boundary()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);

        var cancelled = rig.Runtime.StartJob(
            CapabilityInvocation.Create(
                FixtureProviderV1.SleepCapability,
                new Dictionary<string, object?> { ["milliseconds"] = 5000L }));
        await SupervisorRig.WaitForAsync(rig, () => cancelled.State == JobState.Running, "job running");
        await cancelled.CancelAsync();
        var cancelledResult = await cancelled.WaitForCompletionAsync();
        Assert.True(cancelledResult is CapabilityFailure { Error.Kind: CapabilityErrorKind.Cancelled });
        Assert.Equal(JobState.Cancelled, cancelled.State);

        var timedOut = await rig.InvokeAsync(
            FixtureProviderV1.TimeoutCapability,
            new Dictionary<string, object?> { ["milliseconds"] = 60000L },
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300));
        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, timedOut.Error!.Kind);
        Assert.Equal(
            JobState.TimedOut,
            timedOut.Provenance.JobId is { } id && rig.Runtime.Jobs.TryGet(id, out var job)
                ? job.State
                : JobState.Failed);
    }

    [Fact]
    public async Task Side_by_side_activation_routes_new_work_to_the_new_version()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);
        await rig.Supervisor.ActivateAsync(rig.V2Package);

        // Stable-id order wins without a preference; the active preference flips it.
        var before = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.Equal("fixture@1", before.TryGetValue(out var value) ? value : null);

        rig.Supervisor.RouteNewWorkTo(ProviderId.Parse("fixture@2"));
        var after = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.Equal("fixture@2", after.TryGetValue(out var routed) ? routed : null);
        Assert.Equal(ProviderId.Parse("fixture@2"), rig.Runtime.ActivePreferences[FixtureProviderV1.PeekCapability]);
        Assert.Equal(ResolutionStep.ActivePreferred, after.Provenance.Step);

        rig.Runtime.ClearActivePreference(FixtureProviderV1.PeekCapability);
        var cleared = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.Equal("fixture@1", cleared.TryGetValue(out var back) ? back : null);
    }

    [Fact]
    public async Task Draining_waits_for_inflight_then_stops_the_worker()
    {
        await using var rig = CreateRig();
        var worker = await rig.Supervisor.ActivateAsync(rig.V1Package);

        var minted = await rig.InvokeAsync(FixtureProviderV1.MintCapability);
        var handle = Assert.IsType<ResourceHandle>(minted.TryGetValue(out var value) ? value : null);
        var job = rig.Runtime.StartJob(
            CapabilityInvocation.Create(
                FixtureProviderV1.SleepCapability,
                new Dictionary<string, object?> { ["milliseconds"] = 250L }));

        await rig.Supervisor.DrainAsync(worker.Id);

        Assert.Equal(WorkerState.Stopped, worker.State);
        Assert.True((await job.WaitForCompletionAsync()).IsSuccess);
        Assert.Null(rig.Registry.GetProvider(worker.ProviderId));
        Assert.Equal(ResourceState.Closed, rig.Runtime.Resources.GetState(handle));

        var after = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.Equal(CapabilityErrorKind.CapabilityNotFound, after.Error!.Kind);
        if (worker.ProcessExit is not null)
        {
            Assert.Equal(0, await worker.ProcessExit);
        }
    }

    [Fact]
    public async Task A_crash_is_detected_and_the_worker_restarts()
    {
        await using var rig = CreateRig();
        var worker = await rig.Supervisor.ActivateAsync(rig.V1Package);

        var outcome = await rig.InvokeAsync(FixtureProviderV1.CrashCapability);
        Assert.Equal(CapabilityErrorKind.ProviderFailure, outcome.Error!.Kind);

        await SupervisorRig.WaitForAsync(rig, () => worker.State == WorkerState.Active && worker.RestartCount >= 1, "worker restarted");
        var recovered = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.True(recovered.IsSuccess,
            $"peek after restart failed: {recovered.Error?.Message}; "
            + $"state={worker.State} restarts={worker.RestartCount} lastError={worker.LastError}");
        Assert.True(await rig.Supervisor.HealthCheckOnceAsync(worker.Id));
    }

    [Fact]
    public async Task Repeated_crashes_exhaust_the_restart_policy_and_mark_failed()
    {
        await using var rig = CreateRig(restartPolicy: new RestartPolicy(MaxAttempts: 1));
        var worker = await rig.Supervisor.ActivateAsync(rig.V1Package);

        _ = await rig.InvokeAsync(FixtureProviderV1.CrashCapability);
        await SupervisorRig.WaitForAsync(rig, () => worker.RestartCount == 1 && worker.State == WorkerState.Active, "first restart");
        _ = await rig.InvokeAsync(FixtureProviderV1.CrashCapability);

        await SupervisorRig.WaitForAsync(rig, () => worker.State == WorkerState.Failed, "policy exhausted");
        Assert.Equal(1, worker.RestartCount);
        Assert.Equal(ProviderHealth.Unhealthy, rig.Registry.GetProvider(worker.ProviderId)!.Health);
        Assert.Contains(rig.Supervisor.Events, e => e.Kind == SupervisorEventKind.Failed);
    }

    [Fact]
    public async Task Streaming_flows_through_the_boundary_with_backpressure()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);

        var outcome = await rig.InvokeAsync(
            FixtureProviderV1.StreamCapability,
            new Dictionary<string, object?> { ["chunks"] = 6, ["capacity"] = 2, ["delayMilliseconds"] = 0 });
        var handle = Assert.IsType<ResourceHandle>(outcome.TryGetValue(out var value) ? value : null);

        Assert.True(rig.Runtime.Resources.TryAcquireLease(handle, TimeSpan.FromSeconds(30), out var lease));
        Assert.True(rig.Runtime.Resources.TryOpenStream(handle, lease, out var stream));
        var items = await ReadExactlyAsync(stream, 6);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"chunk-{i}").ToArray(), items);
        var completion = await stream.WaitForCompletionAsync();
        Assert.False(completion.IsFailed);
        Assert.Equal(ResourceState.Leased, rig.Runtime.Resources.GetState(handle));
        Assert.Equal(1, rig.Runtime.Resources.OpenCount);
    }

    [Fact]
    public async Task Rollback_routes_new_work_to_the_previous_version()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);
        await rig.Supervisor.ActivateAsync(rig.V2Package);
        rig.Supervisor.RouteNewWorkTo(ProviderId.Parse("fixture@2"));

        var rolledBack = await rig.Supervisor.RollbackAsync(rig.V1Package);
        Assert.Equal(ProviderId.Parse("fixture@1"), rolledBack.ProviderId);

        var outcome = await rig.InvokeAsync(FixtureProviderV1.PeekCapability);
        Assert.Equal("fixture@1", outcome.TryGetValue(out var value) ? value : null);
        Assert.Contains(rig.Supervisor.Events, e => e.Kind == SupervisorEventKind.RolledBack);
    }

    [Fact]
    public async Task Health_check_loop_probes_workers_until_cancellation()
    {
        await using var rig = CreateRig();
        await rig.Supervisor.ActivateAsync(rig.V1Package);
        using var cts = new CancellationTokenSource();

        var loop = rig.Supervisor.RunHealthChecksAsync(cts.Token);
        await Task.Delay(1300); // let at least one probe + ping interval elapse
        cts.Cancel();
        await loop; // cancellation during the delay ends the loop cleanly, no OCE

        Assert.False(loop.IsCanceled);
        Assert.Equal(WorkerState.Healthy, rig.Supervisor.Workers.Single().State);
        Assert.Contains(rig.Supervisor.Events, e => e.Kind == SupervisorEventKind.Started);
    }

    [Fact]
    public async Task Service_worker_remains_registered_across_a_restart()
    {
        await using var rig = CreateRig();
        var worker = await rig.Supervisor.ActivateAsync(rig.V1Package);

        _ = await rig.InvokeAsync(FixtureProviderV1.CrashCapability);
        await SupervisorRig.WaitForAsync(rig, () => worker.State == WorkerState.Active && worker.RestartCount >= 1, "restarted");
        Assert.NotNull(rig.Registry.GetProvider(worker.ProviderId));
        Assert.Equal(ProviderHealth.Healthy, rig.Registry.GetProvider(worker.ProviderId)!.Health);
    }

    private SupervisorRig CreateRig(
        RestartPolicy? restartPolicy = null,
        int healthFailureThreshold = 3)
    {
        var rig = SupervisorRig.Create(restartPolicy, healthFailureThreshold);
        _rigs.Add(rig);
        return rig;
    }

    private static async Task<IReadOnlyList<object?>> ReadExactlyAsync(ICapabilityStream stream, int expected)
    {
        var items = new List<object?>();
        while (items.Count < expected)
        {
            var batch = await stream.ReadBatchAsync(expected - items.Count);
            if (batch.Count == 0)
            {
                break;
            }

            items.AddRange(batch);
        }

        return items;
    }
}
