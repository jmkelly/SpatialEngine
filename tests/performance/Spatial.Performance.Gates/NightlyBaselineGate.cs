using Xunit.Abstractions;

namespace Spatial.Performance.Gates;

/// <summary>
/// File-backed nightly baseline tests, suite D (T-078). Opt-in only, so
/// the default gate stays fast and offline:
/// <list type="bullet">
/// <item><c>SPATIAL_PERF_BASELINE=1</c> runs the regression gate over
/// <c>&lt;bench-dir&gt;/baseline.json</c> vs the BDN JSON exports under
/// <c>&lt;bench-dir&gt;/raw/</c>, plus the committed budgets.</item>
/// <item><c>SPATIAL_PERF_BASELINE_UPDATE=1</c> promotes the current results
/// to <c>baseline.json</c> (seed on the first nightly, promote on green
/// after).</item>
/// </list>
/// Both skip with an explicit reason when their inputs are absent.
/// </summary>
public sealed class NightlyBaselineGate
{
    private const string GateVariable = "SPATIAL_PERF_BASELINE";

    private const string UpdateVariable = "SPATIAL_PERF_BASELINE_UPDATE";

    private const string GateReason =
        "baseline gate is nightly-only (SPATIAL_PERF_BASELINE=1); skipped in the default gate.";

    private const string UpdateReason =
        "baseline update is explicit (SPATIAL_PERF_BASELINE_UPDATE=1); skipped otherwise.";

    private readonly ITestOutputHelper _output;

    public NightlyBaselineGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public void Full_job_means_hold_baseline_and_budgets()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        var benchDir = BaselineStore.FindBenchDirectory();
        var baselinePath = Path.Combine(benchDir, BaselineStore.BaselineFileName);
        Skip.If(!File.Exists(baselinePath),
            $"no baseline at {baselinePath}; seed it with SPATIAL_PERF_BASELINE_UPDATE=1 first.");

        var rawDir = Path.Combine(benchDir, BaselineStore.RawResultsDirectoryName);
        var parsed = BdnJsonParser.ParseDirectory(rawDir);
        foreach (var warning in parsed.Warnings)
        {
            _output.WriteLine($"parse: {warning}");
        }

        Assert.True(parsed.Samples.Count > 0,
            $"no benchmark samples parsed from {rawDir}; the nightly must run the full BDN job with --exporters json first.");

        var baseline = BaselineStore.LoadSamples(baselinePath);
        var budgets = LoadBudgetsOrEmpty();
        var verdict = BaselineComparer.Compare(baseline, parsed.Samples, budgets);

        foreach (var warning in verdict.Warnings)
        {
            _output.WriteLine($"warn: {warning}");
        }

        _output.WriteLine(
            $"baseline: {baseline.Count} benches, current: {parsed.Samples.Count} benches, " +
            $"new: {verdict.NewBenches.Count}, failures: {verdict.Failures.Count}.");

        Assert.True(verdict.Passed,
            "baseline gate failed:\n- " + string.Join("\n- ", verdict.Failures));
    }

    [SkippableFact]
    public void Update_promotes_current_results_to_baseline()
    {
        Skip.If(Environment.GetEnvironmentVariable(UpdateVariable) != "1", UpdateReason);

        var benchDir = BaselineStore.FindBenchDirectory();
        var rawDir = Path.Combine(benchDir, BaselineStore.RawResultsDirectoryName);
        var parsed = BdnJsonParser.ParseDirectory(rawDir);

        Assert.True(parsed.Samples.Count > 0,
            $"no benchmark samples parsed from {rawDir}; run the full BDN job with --exporters json first.");

        var baselinePath = Path.Combine(benchDir, BaselineStore.BaselineFileName);
        BaselineStore.SaveBaseline(baselinePath, parsed.Samples);
        _output.WriteLine($"baseline: promoted {parsed.Samples.Count} benches to {baselinePath}.");
        foreach (var warning in parsed.Warnings)
        {
            _output.WriteLine($"parse: {warning}");
        }
    }

    private Dictionary<string, BenchBudget> LoadBudgetsOrEmpty()
    {
        var budgetsPath = BaselineStore.FindBudgetsFile();
        if (!File.Exists(budgetsPath))
        {
            _output.WriteLine($"budgets: no file at {budgetsPath}; absolute budget check skipped.");
            return new Dictionary<string, BenchBudget>(StringComparer.Ordinal);
        }

        return BaselineStore.LoadBudgets(budgetsPath);
    }
}
