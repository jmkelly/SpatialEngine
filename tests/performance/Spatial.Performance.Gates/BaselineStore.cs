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

    /// <summary>The solution file that marks the repository root.</summary>
    private const string RootMarker = "SpatialEngine.slnx";

    /// <summary>Bench directory: <c>SPATIAL_BENCH_DIR</c>, else <c>&lt;repo&gt;/artifacts/bench</c>. A relative configured value resolves against the repository root, since the testhost CWD is the test binaries directory while the shell steps run from the root.</summary>
    public static string FindBenchDirectory() =>
        Resolve(Environment.GetEnvironmentVariable("SPATIAL_BENCH_DIR"), ["artifacts", "bench"]);

    /// <summary>Committed budgets file: <c>SPATIAL_BENCH_BUDGETS</c>, else <c>&lt;repo&gt;/tests/performance/bench-budgets.json</c>. Relative values resolve against the repository root, matching <see cref="FindBenchDirectory"/>.</summary>
    public static string FindBudgetsFile() =>
        Resolve(Environment.GetEnvironmentVariable("SPATIAL_BENCH_BUDGETS"), ["tests", "performance", "bench-budgets.json"]);

    /// <summary>The configured value, resolved against the repository root when relative; else the default segments under it.</summary>
    private static string Resolve(string? configured, string[] fallback) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine([FindRepositoryRoot(), .. fallback])
            : Absolute(configured.Trim(), FindRepositoryRoot());

    private static string Absolute(string configured, string repositoryRoot) =>
        Path.IsPathRooted(configured) ? configured : Path.GetFullPath(Path.Combine(repositoryRoot, configured));

    public static string FindRepositoryRoot() =>
        MarkedRoot(new DirectoryInfo(AppContext.BaseDirectory))
        ?? throw new InvalidOperationException($"cannot locate the repository root (no {RootMarker} above the test binaries).");

    /// <summary>Walks up from <paramref name="directory"/> to the first directory holding the solution file.</summary>
    private static string? MarkedRoot(DirectoryInfo? directory) =>
        IsRoot(directory) ? directory!.FullName : MarkedRoot(directory?.Parent);

    private static bool IsRoot(DirectoryInfo? directory) =>
        directory is not null && File.Exists(Path.Combine(directory.FullName, RootMarker));

    /// <summary>Loads a canonical <c>{version, benches: {name: {meanNs, allocatedBytes}}}</c> file.</summary>
    public static Dictionary<string, BenchSample> LoadSamples(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var samples = new Dictionary<string, BenchSample>(StringComparer.Ordinal);
        if (!TryGetBenches(document.RootElement, out var benches))
        {
            return samples;
        }

        return ReadSamples(benches, samples);
    }

    private static Dictionary<string, BenchSample> ReadSamples(JsonElement benches, Dictionary<string, BenchSample> samples)
    {
        foreach (var bench in benches.EnumerateObject())
        {
            AddSample(samples, bench);
        }

        return samples;
    }

    private static void AddSample(Dictionary<string, BenchSample> samples, JsonProperty bench)
    {
        if (TryReadSample(bench, out var sample))
        {
            samples[bench.Name] = sample;
        }
    }

    private static bool TryGetBenches(JsonElement root, out JsonElement benches) =>
        TryProperty(root, "benches", out benches) && IsObject(benches);

    private static bool TryReadSample(JsonProperty bench, out BenchSample sample)
    {
        sample = default!;
        if (!TryNumber(bench.Value, "meanNs", out var mean))
        {
            return false;
        }

        sample = new BenchSample(bench.Name, mean.GetDouble(), ReadNumber(bench.Value, "allocatedBytes"));
        return true;
    }

    private static double ReadNumber(JsonElement value, string propertyName) =>
        TryNumber(value, propertyName, out var number) ? number.GetDouble() : 0;

    private static bool TryNumber(JsonElement value, string propertyName, out JsonElement number) =>
        TryProperty(value, propertyName, out number) && IsNumber(number);

    private static bool TryProperty(JsonElement value, string propertyName, out JsonElement property) =>
        value.TryGetProperty(propertyName, out property);

    private static bool IsNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number;

    private static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    /// <summary>Loads a <c>{name: {budgetNs}}</c> budgets file.</summary>
    public static Dictionary<string, BenchBudget> LoadBudgets(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var budgets = new Dictionary<string, BenchBudget>(StringComparer.Ordinal);
        foreach (var bench in document.RootElement.EnumerateObject())
        {
            AddBudget(budgets, bench);
        }

        return budgets;
    }

    private static void AddBudget(Dictionary<string, BenchBudget> budgets, JsonProperty bench)
    {
        if (TryBudget(bench.Value, out var budgetNs))
        {
            budgets[bench.Name] = new BenchBudget(bench.Name, budgetNs);
        }
    }

    private static bool TryBudget(JsonElement value, out double budgetNs)
    {
        budgetNs = 0;
        return TryNumber(value, "budgetNs", out var budget) && budget.TryGetDouble(out budgetNs);
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
