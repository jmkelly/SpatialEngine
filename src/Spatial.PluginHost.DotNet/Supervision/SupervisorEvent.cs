namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>One recorded supervision event — diagnostics with provenance (plan §22).</summary>
public sealed record SupervisorEvent(
    SupervisorEventKind Kind,
    DateTimeOffset Timestamp,
    Guid WorkerId,
    string Message);

/// <summary>The kind of a supervision event.</summary>
public enum SupervisorEventKind
{
    Discovered = 0,
    Validated = 1,
    Started = 2,
    Healthy = 3,
    Activated = 4,
    Crashed = 5,
    Restarting = 6,
    Failed = 7,
    Draining = 8,
    Stopped = 9,
    RolledBack = 10,
    Diagnostic = 11,
}
