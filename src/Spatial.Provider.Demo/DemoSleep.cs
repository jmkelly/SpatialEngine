using Spatial.PluginSdk.Capabilities;

namespace Spatial.Provider.Demo;

/// <summary>
/// The <c>spatial.demo.sleep@1</c> implementation: sleeps in chunks reporting
/// progress and cancellation — the workbench progress panel's real
/// long-running job (Phase 10, ADR-0031). Mirrors the fixture provider's
/// sleep shape so the job surface behaves identically from a packaged worker.
/// </summary>
internal static class DemoSleep
{
    internal static async ValueTask<CapabilityResult> SleepForAsync(CapabilityInvocation invocation)
    {
        var milliseconds = invocation.TryGetArgument<long>("milliseconds", out var requested) && requested >= 0
            ? requested
            : 300L;

        var step = Math.Min(milliseconds, 25L);
        for (var elapsed = 0L; elapsed < milliseconds; elapsed += step)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(step), invocation.CancellationToken);
            invocation.Progress?.Report(ProgressReport.Create(
                Math.Min(1, (double)(elapsed + step) / milliseconds), $"slept {elapsed + step} of {milliseconds} ms"));
        }

        return CapabilityResult.Success(milliseconds);
    }
}
