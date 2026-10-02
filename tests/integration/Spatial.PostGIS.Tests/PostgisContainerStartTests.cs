namespace Spatial.PostGIS.Tests;

/// <summary>
/// The start policy behind the container fixture (SpatialEngine-o5p): a
/// loaded lane is given a generous budget and several attempts, and a give-up
/// names what happened. These are plain facts — a test that needed a container
/// to prove the retry could not have failed, which is the defect.
/// </summary>
public sealed class PostgisContainerStartTests
{
    [Fact]
    public void The_defaults_are_a_generous_budget_and_more_than_one_attempt()
    {
        Assert.True(
            PostgisContainerStart.DefaultTimeout >= TimeSpan.FromMinutes(2),
            $"a container start is given {PostgisContainerStart.DefaultTimeout}, which is not generous for a loaded box — Testcontainers' own PostGIS wait strategy gives about a minute");
        Assert.True(PostgisContainerStart.DefaultAttempts > 1);
    }

    [Fact]
    public async Task A_start_that_succeeds_on_the_third_attempt_is_a_container_rather_than_a_skip()
    {
        var attempts = 0;
        var prepared = 0;

        var result = await PostgisContainerStart.StartAsync<string>(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? throw new InvalidOperationException("the daemon is busy")
                    : Task.FromResult("container");
            },
            prepareAsync: (_, _) =>
            {
                prepared++;
                return Task.CompletedTask;
            },
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.True(result.Started);
        Assert.Equal("container", result.Container);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(1, prepared);
        Assert.Null(result.Reason);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task An_attempt_that_outlives_its_budget_is_a_timeout_rather_than_a_refusal()
    {
        var waits = new List<TimeSpan>();

        var result = await PostgisContainerStart.StartAsync<object>(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new object();
            },
            attempts: 2,
            timeout: TimeSpan.FromMilliseconds(20),
            delayAsync: (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            });

        Assert.False(result.Started);
        Assert.True(result.TimedOut);
        Assert.Equal(2, result.Attempts);
        Assert.Single(waits);
        Assert.Contains("could not start", result.Reason);
        Assert.Contains("the Docker daemon reachable", result.Reason);
    }

    [Fact]
    public async Task A_refusal_names_the_daemon_and_the_attempts_rather_than_a_timeout()
    {
        var result = await PostgisContainerStart.StartAsync<object>(
            _ => throw new InvalidOperationException("no such image"),
            attempts: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.False(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("the Docker daemon reachable", result.Reason);
        Assert.Contains("no such image", result.Reason);
    }

    [Fact]
    public async Task A_container_whose_preparation_failed_is_discarded_and_retried()
    {
        var discarded = new List<string>();
        var attempts = 0;

        var result = await PostgisContainerStart.StartAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult($"container-{attempts}");
            },
            prepareAsync: (_, _) => attempts == 1
                ? throw new InvalidOperationException("the seed did not apply")
                : Task.CompletedTask,
            discardAsync: (container, _) =>
            {
                discarded.Add(container);
                return Task.CompletedTask;
            },
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.True(result.Started);
        Assert.Equal("container-2", result.Container);
        Assert.Equal(["container-1"], discarded);
    }

    [Fact]
    public async Task A_cancelled_lane_propagates_rather_than_degrading_into_a_skip()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PostgisContainerStart.StartAsync<string>(
                _ =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                },
                attempts: 3,
                delayAsync: (_, _) => Task.CompletedTask,
                cancellationToken: cancellation.Token));
    }
}
