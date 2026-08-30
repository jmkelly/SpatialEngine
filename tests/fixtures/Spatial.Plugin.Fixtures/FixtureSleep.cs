using Spatial.PluginSdk.Capabilities;

namespace Spatial.Plugin.Fixtures;

/// <summary>
/// The sleep implementation shared by the fixture generations: a
/// long-running, cancellable sleep that reports progress in steps — the
/// cancellation (and deadline) fault fixture. Both providers expose it under
/// different capability ids so tests can observe version-specific routing.
/// </summary>
internal static class FixtureSleep
{
    public static async ValueTask<CapabilityResult> SleepFor(CapabilityInvocation invocation, CapabilityId capability)
    {
        if (!invocation.TryGetArgument<long>("milliseconds", out var milliseconds) || milliseconds < 0)
        {
            return CapabilityResult.Failure(CapabilityError.InvalidArguments(
                $"{capability} requires a non-negative int64 argument named 'milliseconds'."));
        }

        const int steps = 10;
        for (var step = 1; step <= steps; step++)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            invocation.Progress?.Report(ProgressReport.Create(step / (double)steps, $"sleep step {step} of {steps}"));
            await Task.Delay((int)(milliseconds / steps), invocation.CancellationToken);
        }

        return CapabilityResult.Success(milliseconds);
    }
}
