using System.Globalization;

namespace Spatial.Host.Tests;

/// <summary>
/// The policy behind the suite's <c>xunit.runner.json</c>: how many test
/// collections may run at once, and what counts as a cap.
/// </summary>
/// <remarks>
/// The host under test boots in process and the first boot in a test process
/// is 13–15 s of CPU-bound work against 0.6 s for every later one (ADR-0154).
/// xunit's default of one thread per core therefore pays that one cost
/// concurrently as many times as there are cores, which is the whole of the
/// suite's measured tail. ADR-0155 measured the fix: a fixed
/// <c>maxParallelThreads</c> of four on a twelve-core box leaves the run's wall
/// clock within a minute of the default and takes 37 % of the CPU out of it,
/// so three or four lanes no longer starve each other.
///
/// The cap is generated, not fixed: a static number cannot sit below nproc on
/// every box — a shipped 4 passes the ceiling on twelve cores and fails it on
/// four — so GenerateXunitRunnerConfig writes <c>xunit.runner.json</c> beside
/// the test assembly with the testing machine's own ceiling (it runs before
/// the VSTest target as well as the build, so a build box and a test box with
/// different core counts still agree), and the suite reads back what the test
/// run wrote (ADR-0196).
/// </remarks>
public static class SuiteParallelism
{
    /// <summary>The <c>xunit.runner.json</c> key this suite's cap lives under.</summary>
    public const string ThreadsKey = "maxParallelThreads";

    /// <summary>
    /// The widest cap a box of <paramref name="cores"/> cores may carry: a
    /// third of it, never fewer than two threads so a small box is not asked
    /// to serialise.
    /// </summary>
    public static int Ceiling(int cores) => Math.Max(2, cores / 3);

    /// <summary>
    /// The threads xunit actually runs, which is never more than the cores it
    /// has — a configuration wider than the box is clamped rather than
    /// honoured, so the cap that counts is the narrower of the two.
    /// </summary>
    public static int Effective(int? threads, int cores) => Math.Min(threads ?? cores, cores);

    /// <summary>
    /// Whether <paramref name="threads"/> is a cap this suite accepts: a
    /// positive count whose effective width on a box of <paramref name="cores"/>
    /// cores is at most <see cref="Ceiling"/>.
    /// </summary>
    public static bool IsBounded(int? threads, int cores) =>
        threads is > 0 && Effective(threads, cores) <= Ceiling(cores);

    /// <summary>The failure message, so a red run says what to write instead.</summary>
    public static string Explain(int? threads, int cores) =>
        $"{ThreadsKey} is {(threads is null ? "absent" : threads.Value.ToString(CultureInfo.InvariantCulture))} on a box with {cores} "
        + $"cores; ADR-0155 measured the suite's run shape at {Ceiling(cores)} threads, because the "
        + "first host boot in a test process is 13–15 s of CPU-bound work (ADR-0154) and xunit's "
        + "default pays it once per core at the same time.";
}
