namespace Spatial.PluginSdk.Jobs;

/// <summary>
/// The lifecycle state of a job (ADR-0008): pending → running → one terminal
/// state. Caller cancellation ends in <see cref="Cancelled"/>; a passed
/// deadline ends in <see cref="TimedOut"/>; provider or contract failures end
/// in <see cref="Failed"/>. Jobs are always cancellable and always reach a
/// terminal state.
/// </summary>
public enum JobState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
}
