namespace Spatial.Performance.Gates;

/// <summary>
/// Nightly baseline gate unit tests, suite D (T-078). Pure logic only:
/// inline BenchmarkDotNet JSON payloads, inline baselines and budgets — no
/// file IO, no network, milliseconds in the default gate. The file-backed
/// nightly tests live in <c>NightlyBaselineGate</c> and are opt-in.
/// </summary>
public sealed class BaselineGateTests
{
    private const string BdnJson = """
        {
          "Title": "probe",
          "Benchmarks": [
            {
              "DisplayInfo": "BufferBenchmarks.Buffer_Engine: MediumRun(IterationCount=15, LaunchCount=2, WarmupCount=10)",
              "Type": "BufferBenchmarks",
              "Method": "Buffer_Engine",
              "MethodTitle": "'Buffer via IGeometryOperations (64-gon)'",
              "Parameters": "",
              "FullName": "Spatial.Performance.BufferBenchmarks.Buffer_Engine",
              "Statistics": { "N": 30, "Mean": 62100.0, "Median": 61000.0 },
              "Memory": { "Gen0Collections": 0, "TotalOperations": 16, "BytesAllocatedPerOperation": 8100.0 }
            },
            {
              "DisplayInfo": "BufferBenchmarks.Buffer_Engine: ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3)",
              "Type": "BufferBenchmarks",
              "Method": "Buffer_Engine",
              "MethodTitle": "'Buffer via IGeometryOperations (64-gon)'",
              "Parameters": "",
              "FullName": "Spatial.Performance.BufferBenchmarks.Buffer_Engine",
              "Statistics": { "N": 3, "Mean": 80000.0, "Median": 79000.0 },
              "Memory": { "Gen0Collections": 0, "TotalOperations": 16, "BytesAllocatedPerOperation": 8100.0 }
            },
            {
              "DisplayInfo": "BufferBenchmarks.Buffer_Raw: MediumRun(IterationCount=15, LaunchCount=2, WarmupCount=10)",
              "Type": "BufferBenchmarks",
              "Method": "Buffer_Raw",
              "MethodTitle": "'Buffer raw NTS (64-gon)'",
              "Parameters": "",
              "FullName": "Spatial.Performance.BufferBenchmarks.Buffer_Raw",
              "Statistics": { "N": 30, "Mean": 56500.0, "Median": 56000.0 },
              "Memory": { "Gen0Collections": 0, "TotalOperations": 16, "BytesAllocatedPerOperation": 7900.0 }
            }
          ]
        }
        """;

    [Fact]
    public void Parser_extracts_bench_key_mean_and_alloc()
    {
        var parsed = BdnJsonParser.ParseReport(BdnJson);

        Assert.Equal(2, parsed.Samples.Count);
        var engine = parsed.Samples["BufferBenchmarks.Buffer_Engine"];
        Assert.Equal(62100.0, engine.MeanNanoseconds);
        Assert.Equal(8100.0, engine.AllocatedBytes);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void Parser_prefers_medium_rows_over_short_rows()
    {
        var parsed = BdnJsonParser.ParseReport(BdnJson);

        // The Short row reports 80,000 ns; the Medium row 62,100 ns. The
        // nightly runs --job Medium over ShortRunJob classes, so both rows
        // exist (T-086) and the gate must compare the Medium means.
        Assert.Equal(62100.0, parsed.Samples["BufferBenchmarks.Buffer_Engine"].MeanNanoseconds);
    }

    [Fact]
    public void Comparer_passes_when_within_tolerance_and_budget()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 60000.0, 8000.0),
            ["BufferBenchmarks.Buffer_Raw"] = new("BufferBenchmarks.Buffer_Raw", 56500.0, 7900.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;
        var budgets = new Dictionary<string, BenchBudget>
        {
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 150000.0),
        };

        var verdict = BaselineComparer.Compare(baseline, current, budgets);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.Empty(verdict.NewBenches);
    }

    [Fact]
    public void Comparer_fails_when_mean_regresses_beyond_15_percent()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            // 62,100 vs 50,000 = +24.2%: over the 15% regression bar.
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 50000.0, 8100.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;

        var verdict = BaselineComparer.Compare(baseline, current, new Dictionary<string, BenchBudget>());

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("BufferBenchmarks.Buffer_Engine") && f.Contains("regression"));
    }

    [Fact]
    public void Comparer_tolerates_regression_within_15_percent()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            // 62,100 vs 55,000 = +12.9%: inside the bar, still green.
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 55000.0, 8100.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;

        var verdict = BaselineComparer.Compare(baseline, current, new Dictionary<string, BenchBudget>());

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
    }

    [Fact]
    public void Comparer_fails_on_alloc_growth_beyond_15_percent()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            // Mean is flat; allocated 8,100 vs 6,000 = +35%: the alloc
            // guard must fire even though the mean is green.
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 62100.0, 6000.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;

        var verdict = BaselineComparer.Compare(baseline, current, new Dictionary<string, BenchBudget>());

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("BufferBenchmarks.Buffer_Engine") && f.Contains("alloc"));
    }

    [Fact]
    public void Comparer_fails_when_over_absolute_budget()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 62100.0, 8100.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;
        var budgets = new Dictionary<string, BenchBudget>
        {
            // 62,100 > 60,000: inside baseline tolerance but over budget.
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 60000.0),
        };

        var verdict = BaselineComparer.Compare(baseline, current, budgets);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("BufferBenchmarks.Buffer_Engine") && f.Contains("budget"));
    }

    [Fact]
    public void Comparer_fails_on_missing_bench_and_notes_new_benches()
    {
        var baseline = new Dictionary<string, BenchSample>
        {
            ["BufferBenchmarks.Buffer_Engine"] = new("BufferBenchmarks.Buffer_Engine", 62100.0, 8100.0),
            ["GoneBenchmarks.Gone_Bench"] = new("GoneBenchmarks.Gone_Bench", 1000.0, 64.0),
        };
        var current = BdnJsonParser.ParseReport(BdnJson).Samples;

        var verdict = BaselineComparer.Compare(baseline, current, new Dictionary<string, BenchBudget>());

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("GoneBenchmarks.Gone_Bench") && f.Contains("missing"));
        // Buffer_Raw is in current but not in the baseline: recorded, not failed.
        Assert.Contains("BufferBenchmarks.Buffer_Raw", verdict.NewBenches);
        Assert.DoesNotContain(verdict.Failures, f => f.Contains("Buffer_Raw"));
    }
}
