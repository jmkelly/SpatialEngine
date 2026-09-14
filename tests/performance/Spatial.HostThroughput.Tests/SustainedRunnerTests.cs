using System.Diagnostics;

namespace Spatial.HostThroughput.Tests;

/// <summary>
/// Unit tests for the sustained-rate runner, suite D (T-078). Pure
/// scheduling and percentile logic against fake operations — always run,
/// fast, no host, no network. The host-backed sustained tests live in
/// <c>HostSustainedRate</c> and are opt-in (<c>SPATIAL_SUSTAINED=1</c>).
/// </summary>
public sealed class SustainedRunnerTests
{
    [Fact]
    public async Task Instant_operation_records_expected_sample_count()
    {
        var result = await SustainedRunner.RunAsync(
            targetRequestsPerSecond: 50,
            duration: TimeSpan.FromMilliseconds(400),
            operation: _ => Task.CompletedTask);

        // 50 rps x 0.4 s = 20 ticks; allow scheduler jitter either way.
        Assert.InRange(result.Offered, 12, 28);
        Assert.Equal(result.Offered, result.Completed);
        Assert.Equal(0, result.Dropped);
        Assert.True(result.P95 < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Percentile_tracks_operation_latency()
    {
        var result = await SustainedRunner.RunAsync(
            targetRequestsPerSecond: 20,
            duration: TimeSpan.FromMilliseconds(500),
            operation: token => Task.Delay(TimeSpan.FromMilliseconds(20), token));

        Assert.True(result.Completed >= 5);
        Assert.InRange(result.P50.TotalMilliseconds, 5, 500);
        Assert.True(result.P95 >= result.P50);
        Assert.True(result.Max >= result.P95);
    }

    [Fact]
    public async Task Cancellation_propagates_and_stops_the_run()
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var wallClock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SustainedRunner.RunAsync(
                targetRequestsPerSecond: 50,
                duration: TimeSpan.FromSeconds(30),
                operation: token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                cancellationToken: cancelled.Token));

        wallClock.Stop();
        Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Saturated_in_flight_cap_counts_dropped_ticks()
    {
        var result = await SustainedRunner.RunAsync(
            targetRequestsPerSecond: 100,
            duration: TimeSpan.FromMilliseconds(500),
            operation: token => Task.Delay(TimeSpan.FromMilliseconds(200), token),
            maxInFlight: 1);

        Assert.True(result.Completed >= 1);
        Assert.True(result.Dropped > 0);
        Assert.Equal(result.Offered, result.Completed + result.Dropped);
    }
}
