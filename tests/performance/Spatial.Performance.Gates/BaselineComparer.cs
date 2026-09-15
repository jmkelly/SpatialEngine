namespace Spatial.Performance.Gates;

/// <summary>
/// The nightly regression rule, suite D (T-078): every bench in the file
/// baseline must still be present, its full-job mean must not regress more
/// than 15% over baseline, its allocated bytes must not grow more than 15%
/// (alloc guard), and budgeted benches must stay under their documented
/// absolute budget. Benches present in the current run but absent from the
/// baseline are recorded as new — they pass, and the baseline update flow
/// absorbs them (see <c>tests/performance/NIGHTLY.md</c>).
/// </summary>
public static class BaselineComparer
{
    /// <summary>Maximum accepted mean growth over baseline before the gate fails.</summary>
    public const double RegressionTolerance = 0.15;

    /// <summary>Maximum accepted allocated-bytes growth over baseline before the gate fails.</summary>
    public const double AllocTolerance = 0.15;

    /// <summary>Gate outcome: failures fail the nightly, warnings and new benches are logged.</summary>
    public sealed record BaselineVerdict(
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> NewBenches)
    {
        public bool Passed => Failures.Count == 0;
    }

    public static BaselineVerdict Compare(
        IReadOnlyDictionary<string, BenchSample> baseline,
        IReadOnlyDictionary<string, BenchSample> current,
        IReadOnlyDictionary<string, BenchBudget> budgets)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        var fresh = new List<string>();

        foreach (var (name, reference) in baseline)
        {
            CheckBench(name, reference, current, failures, warnings);
        }

        foreach (var (name, budget) in budgets)
        {
            CheckBudget(name, budget, current, baseline, failures, warnings);
        }

        CollectNewBenches(baseline, current, fresh, warnings);

        return new BaselineVerdict(failures, warnings, fresh);
    }

    private static void CheckBench(
        string name,
        BenchSample reference,
        IReadOnlyDictionary<string, BenchSample> current,
        List<string> failures,
        List<string> warnings)
    {
        if (!current.TryGetValue(name, out var sample))
        {
            failures.Add($"{name} is missing from the current results (bench removed or renamed?).");
            return;
        }

        CheckRegression(name, reference, sample, failures);
        CheckAllocGuard(name, reference, sample, failures, warnings);
    }

    private static void CheckRegression(string name, BenchSample reference, BenchSample sample, List<string> failures)
    {
        if (sample.MeanNanoseconds > reference.MeanNanoseconds * (1 + RegressionTolerance))
        {
            var growth = (sample.MeanNanoseconds / reference.MeanNanoseconds - 1) * 100;
            failures.Add(
                $"{name} regression +{growth:F1}% over baseline " +
                $"({sample.MeanNanoseconds:F0}ns vs {reference.MeanNanoseconds:F0}ns, bar +{RegressionTolerance * 100:F0}%).");
        }
    }

    private static void CheckAllocGuard(
        string name,
        BenchSample reference,
        BenchSample sample,
        List<string> failures,
        List<string> warnings)
    {
        if (reference.AllocatedBytes > 0
            && sample.AllocatedBytes > reference.AllocatedBytes * (1 + AllocTolerance))
        {
            var growth = (sample.AllocatedBytes / reference.AllocatedBytes - 1) * 100;
            failures.Add(
                $"{name} alloc guard +{growth:F1}% over baseline " +
                $"({sample.AllocatedBytes:F0}B vs {reference.AllocatedBytes:F0}B, bar +{AllocTolerance * 100:F0}%).");
        }
        else if (reference.AllocatedBytes == 0 && sample.AllocatedBytes > 0)
        {
            warnings.Add($"{name} allocates {sample.AllocatedBytes:F0}B/op where the baseline allocates nothing; watch, not fail.");
        }
    }

    private static void CheckBudget(
        string name,
        BenchBudget budget,
        IReadOnlyDictionary<string, BenchSample> current,
        IReadOnlyDictionary<string, BenchSample> baseline,
        List<string> failures,
        List<string> warnings)
    {
        if (current.TryGetValue(name, out var sample))
        {
            if (sample.MeanNanoseconds > budget.BudgetNanoseconds)
            {
                failures.Add(
                    $"{name} over budget ({sample.MeanNanoseconds:F0}ns vs {budget.BudgetNanoseconds:F0}ns).");
            }

            return;
        }

        if (!baseline.ContainsKey(name))
        {
            warnings.Add($"budget for {name} matches no current bench (renamed?).");
        }
    }

    private static void CollectNewBenches(
        IReadOnlyDictionary<string, BenchSample> baseline,
        IReadOnlyDictionary<string, BenchSample> current,
        List<string> fresh,
        List<string> warnings)
    {
        foreach (var name in current.Keys)
        {
            if (!baseline.ContainsKey(name))
            {
                fresh.Add(name);
                warnings.Add($"{name} is new since the baseline; seed it with the baseline update flow.");
            }
        }
    }
}
