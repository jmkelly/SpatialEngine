using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.Runtime.Capabilities;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// The demo sleep (Phase 10, ADR-0031): the workbench's long-running job.
/// Through the runtime it routes as a job with progress, completes with the
/// requested milliseconds, and honours cancellation before it starts.
/// </summary>
public sealed class DemoSleepTests
{
    [Fact]
    public async Task The_sleep_runs_as_a_job_and_returns_the_milliseconds()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.Sleep,
            new Dictionary<string, object?> { ["milliseconds"] = 60L });

        Assert.True(outcome.IsSuccess, outcome.Error?.Message ?? "sleep failed");
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(60L, value);
        Assert.Equal("demo@1", outcome.Provenance.Provider?.ToString());
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
    }

    [Fact]
    public async Task The_sleep_defaults_to_300_milliseconds()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(DemoCapabilities.Sleep);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(300L, value);
    }

    [Fact]
    public async Task A_negative_sleep_falls_back_to_the_default()
    {
        var host = DemoTestHost.Create();

        // TryGetArgument only accepts non-negative int64; a negative value is
        // not readable and the provider serves the documented default (the
        // same shape as the fixture provider's sleep).
        var outcome = await host.InvokeAsync(
            DemoCapabilities.Sleep,
            new Dictionary<string, object?> { ["milliseconds"] = -5L });

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(300L, value);
    }

    [Fact]
    public async Task A_pre_cancelled_sleep_is_a_cancelled_failure()
    {
        var host = DemoTestHost.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var outcome = await host.InvokeAsync(DemoCapabilities.Sleep, cancellationToken: cancelled.Token);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, outcome.Error?.Kind);
    }

    [Fact]
    public async Task The_sleep_reports_progress_events_through_the_job()
    {
        var host = DemoTestHost.Create();
        var job = host.Runtime.StartJob(
            CapabilityInvocation.Create(DemoCapabilities.Sleep, new Dictionary<string, object?> { ["milliseconds"] = 40L }));

        await job.WaitForCompletionAsync();
        Assert.Equal(JobState.Completed, job.State);

        var events = job.Events.Where(entry => entry.Kind == JobEventKind.Progress).ToArray();
        Assert.NotEmpty(events);
        Assert.Contains(events, entry => entry.Progress is { } progress && progress.Fraction is > 0 and <= 1);
    }
}
