namespace Spatial.PluginSdk.Jobs;

/// <summary>
/// The opaque identity of a long-running job (ADR-0008): minted by the
/// runtime when a job is created, tracked by the job registry and carried by
/// the job handle and its invocation provenance. Clients poll or subscribe
/// through the job's <see cref="IJob"/> surface.
/// </summary>
public readonly record struct JobId(Guid Value)
{
    /// <summary>Mints a fresh opaque job id. Ids are never reused.</summary>
    public static JobId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
