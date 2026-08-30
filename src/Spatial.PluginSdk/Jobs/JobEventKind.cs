namespace Spatial.PluginSdk.Jobs;

/// <summary>
/// The kinds of events a job records (ADR-0008 "status, progress and
/// events"): lifecycle transitions, progress observations, published
/// resources and diagnostics. Terminal events carry the same contract
/// surfaces the capability model already uses —
/// <see cref="Spatial.PluginSdk.Capabilities.ProgressReport"/> for progress,
/// <see cref="Spatial.PluginSdk.Capabilities.ICapabilityError"/> for failures
/// and <see cref="Spatial.PluginSdk.Resources.ResourceHandle"/> for published
/// resources — so consumers depend on one model, not a parallel one.
/// </summary>
public enum JobEventKind
{
    Created = 0,
    Started = 1,
    Progress = 2,
    Note = 3,
    Resource = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
    TimedOut = 8,
}
