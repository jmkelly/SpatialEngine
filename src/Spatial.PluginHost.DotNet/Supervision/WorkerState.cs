namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The lifecycle states of one supervised worker (plan §10.4):
/// <c>Discovered → Validated → Starting → Healthy → Active</c> and
/// <c>Draining → Stopped</c>, plus <see cref="Failed"/> for a worker that
/// exhausted its restart policy or never became healthy. A crashed worker
/// transitions through a supervised restart back to <see cref="Active"/>;
/// <see cref="Failed"/> is terminal until a human or host re-activates it.
/// </summary>
public enum WorkerState
{
    Discovered = 0,
    Validated = 1,
    Starting = 2,
    Healthy = 3,
    Active = 4,
    Draining = 5,
    Stopped = 6,
    Failed = 7,
}

public static class WorkerStateSupport
{
    /// <summary>Whether a worker in this state can serve invocations.</summary>
    public static bool CanServe(WorkerState state) =>
        state is WorkerState.Healthy or WorkerState.Active;

    /// <summary>Whether this state is terminal (no further automatic transitions).</summary>
    public static bool IsTerminal(WorkerState state) =>
        state is WorkerState.Stopped or WorkerState.Failed;
}
