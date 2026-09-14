using System.Diagnostics;

namespace Spatial.HostThroughput.Tests;

/// <summary>
/// Open-loop sustained-rate driver, suite D (T-078): offers a constant
/// request rate for a fixed duration and records per-request latency.
/// Unlike the burst smoke (<c>HostThroughputSmoke</c>, fixed iteration
/// count at max parallelism), this holds continuous load — the T-091
/// sustained-rate requirement folded into T-078 — so the T-077 p95 budgets
/// are confirmed under steady state, not just bursts.
///
/// Long-running work stays a cancellable <c>Task</c>: the tick loop and
/// every operation take the caller's <c>CancellationToken</c>, and an
/// external cancel propagates as <c>OperationCanceledException</c> instead
/// of a verdict.
/// </summary>
public static class SustainedRunner
{
    /// <summary>
    /// Offers <paramref name="targetRequestsPerSecond"/> for
    /// <paramref name="duration"/>, recording each successful operation's
    /// latency. Ticks that arrive while <paramref name="maxInFlight"/>
    /// operations are already running are counted as dropped (backpressure
    /// valve, not queueing). Failed operations propagate through the drain;
    /// cancelled runs throw <c>OperationCanceledException</c>.
    /// </summary>
    public static async Task<SustainedResult> RunAsync(
        double targetRequestsPerSecond,
        TimeSpan duration,
        Func<CancellationToken, Task> operation,
        int maxInFlight = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetRequestsPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxInFlight, 0);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / targetRequestsPerSecond));
        using var inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
        var tasks = new List<Task>();
        var latencies = new List<double>();
        var offered = 0;
        var dropped = 0;
        var wallClock = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            await timer.WaitForNextTickAsync(cancellationToken);
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            offered++;
            // Non-blocking try-acquire by design: a full flight counts the
            // tick as dropped (CA2016: CancellationToken.None is explicit).
            if (!inFlight.Wait(0, CancellationToken.None))
            {
                dropped++;
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                var operationClock = Stopwatch.StartNew();
                try
                {
                    await operation(cancellationToken);
                    operationClock.Stop();
                    lock (latencies)
                    {
                        latencies.Add(operationClock.Elapsed.TotalMilliseconds);
                    }
                }
                finally
                {
                    inFlight.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        wallClock.Stop();

        double[] sorted;
        lock (latencies)
        {
            sorted = latencies.ToArray();
        }

        Array.Sort(sorted);
        return new SustainedResult(
            Offered: offered,
            Completed: sorted.Length,
            Dropped: dropped,
            P50: TimeSpan.FromMilliseconds(Percentile(sorted, 0.50)),
            P95: TimeSpan.FromMilliseconds(Percentile(sorted, 0.95)),
            P99: TimeSpan.FromMilliseconds(Percentile(sorted, 0.99)),
            Max: sorted.Length == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(sorted[^1]),
            AchievedRps: sorted.Length / wallClock.Elapsed.TotalSeconds,
            Elapsed: wallClock.Elapsed);
    }

    private static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1);
        return sorted[Math.Max(0, rank)];
    }
}

/// <summary>Sustained window outcome: offered vs completed load plus the latency histogram.</summary>
public sealed record SustainedResult(
    int Offered,
    int Completed,
    int Dropped,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Max,
    double AchievedRps,
    TimeSpan Elapsed);
