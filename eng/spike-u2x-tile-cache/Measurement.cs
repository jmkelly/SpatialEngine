namespace Spatial.Spike.TileCache;

/// <summary>
/// One timed operation: wall time, CPU time and the managed bytes allocated
/// inside it. Same shape as the u2x.1 spike's <c>Measurement</c>, plus CPU time
/// — added here because wall time alone could not settle this bead.
///
/// Why CPU time is carried: the measurement box is shared with a parallel agent
/// swarm, and the wall-time ORDERING of the two arms was observed to flip with
/// box load (compose cheaper than a whole-map cold render in two runs, dearer
/// in a third). CPU time is the process's own consumption, so it is far less
/// sensitive to being descheduled by a neighbour, and it is the quantity the
/// decision is actually about — the work the host does per served tile, not the
/// time a client happened to wait for it.
/// </summary>
internal readonly record struct Sample(double Milliseconds, double CpuMilliseconds, long AllocatedBytes);

/// <summary>
/// The timing loop. Warmup runs first (JIT, Skia's first rasterisation, the
/// store's first query) and is not measured; each measured iteration starts
/// from a forced collection so the allocated-bytes delta belongs to that call
/// alone.
/// </summary>
internal static class Measurement
{
    internal static async Task<IReadOnlyList<Sample>> RunAsync(
        Func<CancellationToken, Task> operation,
        int warmup,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < warmup; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation(cancellationToken);
        }

        var samples = new List<Sample>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await OnceAsync(() => operation(cancellationToken)));
        }

        return samples;
    }

    private static async Task<Sample> OnceAsync(Func<Task> operation)
    {
        Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await operation();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        return new Sample(
            elapsed.TotalMilliseconds,
            cpu.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
    }

    private static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
}

/// <summary>Percentiles over a set of samples.</summary>
internal sealed record SampleReport(
    string Scenario,
    string Variant,
    int Iterations,
    double P50Milliseconds,
    double P95Milliseconds,
    double MeanMilliseconds,
    double P50CpuMilliseconds,
    long AllocatedBytes)
{
    internal static SampleReport From(string scenario, string variant, IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0)
        {
            return new SampleReport(scenario, variant, 0, 0, 0, 0, 0, 0);
        }

        var ordered = samples.Select(sample => sample.Milliseconds).Order().ToArray();
        var orderedCpu = samples.Select(sample => sample.CpuMilliseconds).Order().ToArray();
        return new SampleReport(
            scenario,
            variant,
            samples.Count,
            Round(Percentile(ordered, 0.50)),
            Round(Percentile(ordered, 0.95)),
            Round(ordered.Average()),
            Round(Percentile(orderedCpu, 0.50)),
            (long)samples.Average(sample => sample.AllocatedBytes));
    }

    private static double Percentile(double[] ordered, double fraction)
    {
        if (ordered.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * ordered.Length) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    internal string ToRow() =>
        $"{Scenario,-10} {Variant,-16} {Iterations,5} " +
        $"{P50Milliseconds,12:F3} {P95Milliseconds,12:F3} {MeanMilliseconds,12:F3} " +
        $"{P50CpuMilliseconds,12:F1} {AllocatedBytes / 1024.0 / 1024.0,10:F2}";

    internal const string Header =
        "scenario   variant              iters       p50_ms       p95_ms      mean_ms     p50_cpu_ms     alloc_MB";
}
