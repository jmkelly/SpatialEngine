namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The restart policy for crashed workers (plan §10.2 "independent restart"):
/// a bounded number of restart attempts with an exponential backoff, after
/// which the worker is marked <see cref="WorkerState.Failed"/> and requires
/// human or host attention. Restarting is a supervision decision, so the
/// policy is pure rules — no side effects.
/// </summary>
public sealed record RestartPolicy(int MaxAttempts = 3)
{
    public static RestartPolicy Default { get; } = new();

    /// <summary>The backoff before the next restart attempt: 100 ms, 200 ms, 400 ms, … capped at ~6 s.</summary>
    public static TimeSpan BackoffFor(int restartCount) =>
        restartCount <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(100 * (1 << Math.Min(restartCount - 1, 6)));

    /// <summary>Whether another restart attempt is allowed after <paramref name="restartCount"/> attempts.</summary>
    public bool ShouldRestart(int restartCount) => restartCount < MaxAttempts;
}
