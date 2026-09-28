namespace Spatial.Spike.QueryBaseline;

/// <summary>
/// One measured call: wall time, managed bytes allocated inside the call, the
/// peak managed heap observed right after it, how many rows the call
/// materialised into <c>FeatureBatch</c> pages, and how big the result was.
/// The row count is the number the bead asks for — the difference between
/// paths is mostly a difference in how many rows are built at all.
/// </summary>
internal sealed record Sample(
    double Milliseconds,
    long AllocatedBytes,
    long PeakHeapBytes,
    int RowsMaterialised,
    long ResultRows);

/// <summary>What a measured operation reports back: rows built, and the size of the result it produced.</summary>
internal readonly record struct Outcome(int RowsMaterialised, long ResultRows);

/// <summary>
/// The timing/allocation loop. Warmup runs first (JIT, the store's first
/// query plan, the TSV parse) and is not measured; each measured iteration
/// starts from a forced collection so the allocated-bytes delta and the peak
/// heap belong to that call alone.
/// </summary>
internal static class Measurement
{
    private const int SortSampleLimit = 64;

    internal static async Task<IReadOnlyList<Sample>> RunAsync(
        Func<Task<Outcome>> operation,
        int warmup,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < warmup; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }

        var samples = new List<Sample>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await OnceAsync(operation));
        }

        return samples;
    }

    private static async Task<Sample> OnceAsync(Func<Task<Outcome>> operation)
    {
        Collect();
        var liveBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = await operation();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var peak = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - liveBefore);
        return new Sample(elapsed.TotalMilliseconds, allocated, peak, outcome.RowsMaterialised, outcome.ResultRows);
    }

    private static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
}

/// <summary>Percentiles and totals over a set of samples.</summary>
internal sealed record SampleReport(
    string Path,
    string Scenario,
    string Variant,
    int Iterations,
    double ColdMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double MeanMilliseconds,
    long AllocatedBytes,
    long PeakHeapBytes,
    int RowsMaterialised,
    long ResultRows)
{
    internal static SampleReport From(string path, string scenario, string variant, IReadOnlyList<Sample> samples)
    {
        var ordered = samples.Select(sample => sample.Milliseconds).Order().ToArray();
        if (ordered.Length == 0)
        {
            return new SampleReport(path, scenario, variant, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new SampleReport(
            path,
            scenario,
            variant,
            samples.Count,
            Round(ordered[0]),
            Round(Percentile(ordered, 0.50)),
            Round(Percentile(ordered, 0.95)),
            Round(ordered.Average()),
            (long)samples.Average(sample => sample.AllocatedBytes),
            samples.Max(sample => sample.PeakHeapBytes),
            samples.Max(sample => sample.RowsMaterialised),
            samples.Max(sample => sample.ResultRows));
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
        $"{Path,-8} {Scenario,-7} {Variant,-10} " +
        $"{ColdMilliseconds,10:F3} {P50Milliseconds,10:F3} {P95Milliseconds,10:F3} {MeanMilliseconds,10:F3} " +
        $"{AllocatedBytes / 1024.0 / 1024.0,10:F2} {PeakHeapBytes / 1024.0 / 1024.0,10:F2} " +
        $"{RowsMaterialised,10} {ResultRows,10}";

    internal const string Header =
        "path     scenario variant     cold_ms     p50_ms     p95_ms    mean_ms      alloc_MB    peak_MB       rows    result";
}
