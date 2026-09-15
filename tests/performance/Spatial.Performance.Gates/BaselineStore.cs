using System.Text.Json;

namespace Spatial.Performance.Gates;

/// <summary>
/// File layout for the nightly baselines, suite D (T-078). Baselines live
/// in <c>artifacts/bench/</c> (gitignored): <c>baseline.json</c> holds the
/// last green means, <c>raw/</c> holds the BenchmarkDotNet JSON exports.
/// The nightly workflow restores the previous <c>baseline.json</c> from the
/// last green run's artifact and promotes the new one only on green.
/// </summary>
public static class BaselineStore
{
    public const string BaselineFileName = "baseline.json";

    public const string RawResultsDirectoryName = "raw";

    /// <summary>Bench directory: <c>SPATIAL_BENCH_DIR</c>, else <c>&lt;repo&gt;/artifacts/bench</c>. A relative configured value resolves against the repository root, since the testhost CWD is the test binaries directory while the shell steps run from the root.</summary>
    public static string FindBenchDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SPATIAL_BENCH_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            return Path.GetFullPath(Path.Combine(FindRepositoryRoot(), trimmed));
        }

        return Path.Combine(FindRepositoryRoot(), "artifacts", "bench");
    }

    /// <summary>Committed budgets file: <c>SPATIAL_BENCH_BUDGETS</c>, else <c>&lt;repo&gt;/tests/performance/bench-budgets.json</c>. Relative values resolve against the repository root, matching <see cref="FindBenchDirectory"/>.</summary>
    public static string FindBudgetsFile()
    {
        var configured = Environment.GetEnvironmentVariable("SPATIAL_BENCH_BUDGETS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            return Path.GetFullPath(Path.Combine(FindRepositoryRoot(), trimmed));
        }

        return Path.Combine(FindRepositoryRoot(), "tests", "performance", "bench-budgets.json");
    }

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpatialEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("cannot locate the repository root (no SpatialEngine.slnx above the test binaries).");
    }

    /// <summary>Loads a canonical <c>{version, benches: {name: {meanNs, allocatedBytes}}}</c> file.</summary>
    public static Dictionary<string, BenchSample> LoadSamples(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var samples = new Dictionary<string, BenchSample>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("benches", out var benches)
            || benches.ValueKind != JsonValueKind.Object)
        {
            return samples;
        }

        foreach (var bench in benches.EnumerateObject())
        {
            if (bench.Value.TryGetProperty("meanNs", out var mean)
                && mean.ValueKind == JsonValueKind.Number)
            {
                var allocated = 0.0;
                if (bench.Value.TryGetProperty("allocatedBytes", out var alloc)
                    && alloc.ValueKind == JsonValueKind.Number)
                {
                    allocated = alloc.GetDouble();
                }

                samples[bench.Name] = new BenchSample(bench.Name, mean.GetDouble(), allocated);
            }
        }

        return samples;
    }

    /// <summary>Loads a <c>{name: {budgetNs}}</c> budgets file.</summary>
    public static Dictionary<string, BenchBudget> LoadBudgets(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var budgets = new Dictionary<string, BenchBudget>(StringComparer.Ordinal);
        foreach (var bench in document.RootElement.EnumerateObject())
        {
            if (bench.Value.TryGetProperty("budgetNs", out var budget)
                && budget.ValueKind == JsonValueKind.Number)
            {
                budgets[bench.Name] = new BenchBudget(bench.Name, budget.GetDouble());
            }
        }

        return budgets;
    }

    /// <summary>Writes the canonical baseline file, creating the directory.</summary>
    public static void SaveBaseline(string path, IReadOnlyDictionary<string, BenchSample> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteStartObject("benches");
        foreach (var sample in samples.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject(sample.Name);
            writer.WriteNumber("meanNs", sample.MeanNanoseconds);
            writer.WriteNumber("allocatedBytes", sample.AllocatedBytes);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
