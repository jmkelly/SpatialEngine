namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// Declared runtime behaviour of a capability (plan §9 "streaming and
/// cancellation behaviour" and "side effects"). Traits are part of the
/// contract: the runtime enforces the coherence rule that long-running
/// capabilities must be cancellable (ADR-0008).
/// </summary>
[Flags]
public enum CapabilityTraits
{
    /// <summary>No special runtime behaviour declared.</summary>
    None = 0,

    /// <summary>The capability can be cancelled mid-invocation.</summary>
    Cancellable = 1 << 0,

    /// <summary>The capability streams results (progress and partial output).</summary>
    Streaming = 1 << 1,

    /// <summary>
    /// The capability typically runs for seconds or minutes and should be
    /// routed through the job model (Phase 4, ADR-0008). Long-running
    /// capabilities must also declare <see cref="Cancellable"/>.
    /// </summary>
    LongRunning = 1 << 2,

    /// <summary>The capability mutates state or emits effects beyond its result.</summary>
    SideEffects = 1 << 3,
}