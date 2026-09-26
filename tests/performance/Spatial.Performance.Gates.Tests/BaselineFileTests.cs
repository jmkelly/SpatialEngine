using Spatial.Performance.Gates;

namespace Spatial.Performance.Gates.Tests;

/// <summary>
/// File-layout tests for the nightly baseline gate, suite D (T-078): the
/// baseline/budgets JSON and the BDN export directory, written to a temp
/// directory per test. The opt-in nightly gate itself lives in
/// <see cref="NightlyBaselineGate"/> and needs the nightly artifacts.
/// </summary>
public sealed class BaselineFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"spatial-gates-{Guid.NewGuid():N}");

    public BaselineFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Baseline_round_trips_through_save_and_load()
    {
        var path = Path.Combine(_directory, BaselineStore.BaselineFileName);
        var samples = new Dictionary<string, BenchSample>(StringComparer.Ordinal)
        {
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 62100.0, 8100.0),
            ["BufferBenchmarks.Buffer_Raw"] = new("BufferBenchmarks.Buffer_Raw", 56500.0, 7900.0),
        };

        BaselineStore.SaveBaseline(path, samples);
        var loaded = BaselineStore.LoadSamples(path);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(62100.0, loaded["BufferBenchmarks.Buffer_Engine"].MeanNanoseconds);
        Assert.Equal(8100.0, loaded["BufferBenchmarks.Buffer_Engine"].AllocatedBytes);
    }

    [Fact]
    public void Loading_a_baseline_without_benches_yields_no_samples()
    {
        var path = Write("baseline.json", """{ "version": 1 }""");

        var loaded = BaselineStore.LoadSamples(path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void Budgets_file_reads_only_numbered_budgets()
    {
        var path = Write("bench-budgets.json", """
            {
              "BufferBenchmarks.Buffer_Engine": { "budgetNs": 150000.0 },
              "BufferBenchmarks.Buffer_Raw": { "budgetNs": "fast" }
            }
            """);

        var budgets = BaselineStore.LoadBudgets(path);

        // The non-numeric budget is skipped rather than read as zero.
        Assert.Equal(["BufferBenchmarks.Buffer_Engine"], budgets.Keys);
        Assert.Equal(150000.0, budgets["BufferBenchmarks.Buffer_Engine"].BudgetNanoseconds);
    }

    [Fact]
    public void Parsing_a_results_directory_merges_every_report()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "nested"));
        File.WriteAllText(
            Path.Combine(_directory, "b.json"),
            Report("BufferBenchmarks", "Buffer_Engine", 62100.0, "MediumRun"));
        File.WriteAllText(
            Path.Combine(_directory, "nested", "a.json"),
            Report("BufferBenchmarks", "Buffer_Raw", 56500.0, "MediumRun"));

        var parsed = BdnJsonParser.ParseDirectory(_directory);

        Assert.Equal(2, parsed.Samples.Count);
        Assert.Equal(62100.0, parsed.Samples["BufferBenchmarks.Buffer_Engine"].MeanNanoseconds);
        Assert.Equal(56500.0, parsed.Samples["BufferBenchmarks.Buffer_Raw"].MeanNanoseconds);
    }

    [Fact]
    public void Parsing_a_missing_results_directory_warns_instead_of_failing()
    {
        var parsed = BdnJsonParser.ParseDirectory(Path.Combine(_directory, "absent"));

        Assert.Empty(parsed.Samples);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Parsing_an_empty_results_directory_warns_instead_of_failing()
    {
        var parsed = BdnJsonParser.ParseDirectory(_directory);

        Assert.Empty(parsed.Samples);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("no JSON reports", StringComparison.Ordinal));
    }

    [Fact]
    public void Parsing_an_unreadable_report_warns_with_the_file_name()
    {
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not json");

        var parsed = BdnJsonParser.ParseDirectory(_directory);

        Assert.Empty(parsed.Samples);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("broken.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Parsing_a_report_without_benchmarks_warns()
    {
        var parsed = BdnJsonParser.ParseReport("""{ "Title": "probe" }""");

        Assert.Empty(parsed.Samples);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("no Benchmarks", StringComparison.Ordinal));
    }

    [Fact]
    public void Parsing_skips_rows_without_type_method_or_mean()
    {
        var parsed = BdnJsonParser.ParseReport("""
            {
              "Benchmarks": [
                { "Statistics": { "Mean": 1000.0 } },
                { "Type": "BufferBenchmarks", "Method": "No_Mean", "Statistics": {} },
                { "Type": "BufferBenchmarks", "Method": "Buffer_Engine",
                  "Statistics": { "Mean": 62100.0 }, "Memory": { "BytesAllocatedPerOperation": 8100.0 } }
              ]
            }
            """);

        Assert.Equal(["BufferBenchmarks.Buffer_Engine"], parsed.Samples.Keys);
        Assert.Equal(2, parsed.Warnings.Count);
    }

    [Fact]
    public void Parsing_keeps_the_medium_row_and_the_first_of_two_equal_ranks()
    {
        var parsed = BdnJsonParser.ParseReport("""
            {
              "Benchmarks": [
                { "DisplayInfo": "Buffer_Engine: ShortRun", "Type": "BufferBenchmarks", "Method": "Buffer_Engine",
                  "Statistics": { "Mean": 80000.0 } },
                { "DisplayInfo": "Buffer_Engine: MediumRun", "Type": "BufferBenchmarks", "Method": "Buffer_Engine",
                  "Statistics": { "Mean": 62100.0 } },
                { "DisplayInfo": "Buffer_Engine: ShortRun", "Type": "BufferBenchmarks", "Method": "Buffer_Engine",
                  "Statistics": { "Mean": 80000.0 } }
              ]
            }
            """);

        Assert.Equal(62100.0, parsed.Samples["BufferBenchmarks.Buffer_Engine"].MeanNanoseconds);
    }

    [Fact]
    public void Parsing_reads_a_row_without_memory_as_allocating_nothing()
    {
        var parsed = BdnJsonParser.ParseReport(
            Report("BufferBenchmarks", "Buffer_Engine", 62100.0, "MediumRun", allocated: null));

        Assert.Equal(0, parsed.Samples["BufferBenchmarks.Buffer_Engine"].AllocatedBytes);
    }

    [Fact]
    public void Bench_directory_falls_back_to_the_repository_artifacts()
    {
        var previous = Environment.GetEnvironmentVariable("SPATIAL_BENCH_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_DIR", null);

            var resolved = BaselineStore.FindBenchDirectory();

            Assert.Equal(
                Path.Combine(BaselineStore.FindRepositoryRoot(), "artifacts", "bench"),
                resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_DIR", previous);
        }
    }

    [Fact]
    public void Budgets_file_falls_back_to_the_committed_file()
    {
        var previous = Environment.GetEnvironmentVariable("SPATIAL_BENCH_BUDGETS");
        try
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_BUDGETS", null);

            var resolved = BaselineStore.FindBudgetsFile();

            Assert.Equal(
                Path.Combine(BaselineStore.FindRepositoryRoot(), "tests", "performance", "bench-budgets.json"),
                resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_BUDGETS", previous);
        }
    }

    [Fact]
    public void A_rooted_bench_directory_is_taken_verbatim()
    {
        var previous = Environment.GetEnvironmentVariable("SPATIAL_BENCH_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_DIR", _directory);

            Assert.Equal(_directory, BaselineStore.FindBenchDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPATIAL_BENCH_DIR", previous);
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Report(
        string type, string method, double mean, string displayInfo, double? allocated = 8100.0) =>
        $$"""
          {
            "Benchmarks": [
              {
                "DisplayInfo": "{{type}}.{{method}}: {{displayInfo}}",
                "Type": "{{type}}",
                "Method": "{{method}}",
                "Statistics": { "Mean": {{mean}} }{{Memory(allocated)}}
              }
            ]
          }
          """;

    private static string Memory(double? allocated) =>
        allocated is null
            ? string.Empty
            : $", \"Memory\": {{ \"BytesAllocatedPerOperation\": {allocated.Value} }}";
}
