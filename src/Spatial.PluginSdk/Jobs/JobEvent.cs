using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Jobs;

/// <summary>
/// One observable job event: a kind, a timestamp and the event's payload — a
/// <see cref="Capabilities.ProgressReport"/> for progress events, an
/// <see cref="Capabilities.ICapabilityError"/> for failure events or a
/// <see cref="Resources.ResourceHandle"/> for published resources. Events are
/// append-only; consumers take snapshots from the job handle.
/// </summary>
public sealed record JobEvent(
    JobEventKind Kind,
    DateTimeOffset Timestamp,
    ProgressReport? Progress = null,
    ICapabilityError? Error = null,
    ResourceHandle? Resource = null,
    string? Note = null)
{
    public static JobEvent Created() => new(JobEventKind.Created, DateTimeOffset.UtcNow);

    public static JobEvent Started() => new(JobEventKind.Started, DateTimeOffset.UtcNow);

    public static JobEvent ReportProgress(ProgressReport report) =>
        new(JobEventKind.Progress, DateTimeOffset.UtcNow, Progress: report);

    /// <summary>A diagnostic note attached to a running job.</summary>
    public static JobEvent Diagnostic(string note) => new(JobEventKind.Note, DateTimeOffset.UtcNow, Note: note);

    /// <summary>A resource the job published (for example a stream handle to read while the job runs).</summary>
    public static JobEvent PublishResource(ResourceHandle resource) =>
        new(JobEventKind.Resource, DateTimeOffset.UtcNow, Resource: resource);

    public static JobEvent Completed() => new(JobEventKind.Completed, DateTimeOffset.UtcNow);

    public static JobEvent Failed(ICapabilityError error) =>
        new(JobEventKind.Failed, DateTimeOffset.UtcNow, Error: error);

    public static JobEvent Cancelled() => new(JobEventKind.Cancelled, DateTimeOffset.UtcNow);

    public static JobEvent TimedOut(ICapabilityError error) =>
        new(JobEventKind.TimedOut, DateTimeOffset.UtcNow, Error: error);
}
