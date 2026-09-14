namespace Spatial.Performance.Gates;

/// <summary>
/// One benchmark row the nightly gate compares: the full-job mean in
/// nanoseconds plus the allocated bytes per operation (the alloc guard).
/// </summary>
/// <param name="Name">Canonical bench key: <c>Type.Method</c>, e.g. <c>BufferBenchmarks.Buffer_Engine</c>.</param>
/// <param name="MeanNanoseconds">Full-job (Medium) mean from <c>Statistics.Mean</c>.</param>
/// <param name="AllocatedBytes">Allocated bytes per operation from <c>Memory.BytesAllocatedPerOperation</c>.</param>
public sealed record BenchSample(string Name, double MeanNanoseconds, double AllocatedBytes);

/// <summary>Documented absolute upper bound for a bench mean (see <c>tests/performance/BUDGETS.md</c>).</summary>
public sealed record BenchBudget(string Name, double BudgetNanoseconds);
