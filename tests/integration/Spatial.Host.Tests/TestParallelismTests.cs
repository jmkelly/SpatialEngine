using System.Text.Json;

namespace Spatial.Host.Tests;

/// <summary>
/// The suite's own run shape: how many xunit collections it lets run at
/// once, read out of the <c>xunit.runner.json</c> the runner actually reads
/// from the output directory.
///
/// </summary>
/// <remarks>
/// The host boots in process, and the <em>first</em> boot in a test process is
/// 13–15 s of CPU-bound work while every later boot is 0.6 s (ADR-0154). xunit
/// parallelises by collection, so on an idle box a dozen collections reach
/// <c>CreateClient()</c> together and the one process-wide cost is paid
/// concurrently a dozen times: the whole suite asks 6.8 of the box's 12 cores,
/// eight unrelated tests carry a ~20 s floor for no work of their own, and on a
/// box running three or four swarm lanes the tail runs into minutes and comes
/// back as the <c>TaskCanceledException</c> ADR-0155 measures away.
///
/// So the runner configuration is capped below <c>nproc</c> — generated for
/// the machine that runs the tests, because a static file cannot be computed
/// from the box it runs on — and these tests are what stop it drifting back
/// to the default: a missing file, a file that never reached the output
/// directory (so the runner quietly used its own default), a cap wider than
/// this box's ceiling, or a cap the build wrote that the policy would not
/// accept.
/// </remarks>
public sealed class TestParallelismTests
{
    /// <summary>The runner reads its configuration from beside the test assembly.</summary>
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");

    [Fact]
    public void The_runner_configuration_reached_the_output_directory()
    {
        Assert.True(
            File.Exists(ConfigPath),
            $"no {nameof(SuiteParallelism)} configuration at {ConfigPath}: the runner falls back to "
            + $"maxParallelThreads = nproc, which is the shape ADR-0154 measured. Check the "
            + "GenerateXunitRunnerConfig target in Spatial.Host.Tests.csproj.");

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var threads = document.RootElement.GetProperty(SuiteParallelism.ThreadsKey).GetInt32();

        Assert.True(
            SuiteParallelism.IsBounded(threads, Environment.ProcessorCount),
            SuiteParallelism.Explain(threads, Environment.ProcessorCount));
    }

    [Fact]
    public void A_cap_narrower_than_the_box_is_the_default_and_a_wider_one_is_not_accepted()
    {
        const int TwelveCores = 12;

        // The shape this suite runs in: a third of the box is what ADR-0155
        // measured as the knee.
        Assert.True(SuiteParallelism.IsBounded(4, TwelveCores));

        // xunit's own default is one thread per core, which is what put eight
        // cold starts on the box at once.
        Assert.False(SuiteParallelism.IsBounded(TwelveCores, TwelveCores));
        Assert.False(SuiteParallelism.IsBounded(TwelveCores + 4, TwelveCores));

        // Not a cap at all.
        Assert.False(SuiteParallelism.IsBounded(null, TwelveCores));
        Assert.False(SuiteParallelism.IsBounded(0, TwelveCores));
        Assert.False(SuiteParallelism.IsBounded(-1, TwelveCores));
    }

    [Fact]
    public void The_cap_never_serialises_a_small_box()
    {
        // A two-core laptop is not asked to run the suite one test at a time,
        // and the fixed cap the file carries reads as a cap on it: xunit
        // clamps a thread count to the cores it has, so four on two cores runs
        // two collections at a time rather than serialising the suite.
        Assert.Equal(2, SuiteParallelism.Effective(4, 2));
        Assert.True(SuiteParallelism.IsBounded(2, 2));
        Assert.True(SuiteParallelism.IsBounded(4, 2));
        Assert.True(SuiteParallelism.IsBounded(2, 1));
        Assert.Equal(1, SuiteParallelism.Effective(4, 1));
    }

    [Fact]
    public void The_generated_cap_is_this_box_s_ceiling()
    {
        // GenerateXunitRunnerConfig runs before the VSTest target as well as
        // the build, so the shipped number is the testing machine's own
        // ceiling (ADR-0196): a static file passed on the box it was measured
        // on and failed on anything smaller.
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var threads = document.RootElement.GetProperty(SuiteParallelism.ThreadsKey).GetInt32();

        Assert.Equal(SuiteParallelism.Ceiling(Environment.ProcessorCount), threads);
    }

    [Fact]
    public async Task A_cancelled_request_is_still_cancellation_and_not_a_timeout()
    {
        // The signature ADR-0154 named — a TaskCanceledException raised while
        // copying a response body — is what a loaded box produced. Bounding
        // the suite's parallelism changes how much work the box is asked for,
        // not how a cancelled request is reported, and this is the test that
        // says so.
        using var factory = new PostgisHostFactory();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/health/ready", cancellation.Token));
    }
}
