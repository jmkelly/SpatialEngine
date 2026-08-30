namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The read-only view of a capability invocation that providers receive:
/// the requested capability, typed arguments, granted permissions, the
/// optional deadline, the progress sink and the cancellation token.
/// <see cref="CapabilityInvocation"/> implements it; Phase 4 job handles
/// expose the same surface, so providers depend on the abstraction, not on a
/// specific context implementation.
/// </summary>
public interface IInvocationContext
{
    CapabilityId Capability { get; }

    IReadOnlyDictionary<string, object?> Arguments { get; }

    IReadOnlySet<Permission> GrantedPermissions { get; }

    DateTimeOffset? Deadline { get; }

    IProgress<ProgressReport>? Progress { get; }

    CancellationToken CancellationToken { get; }

    /// <summary>
    /// The runtime-backed facilities for this invocation (resource minting
    /// and bounded streams), or null when the invocation was constructed
    /// without a runtime. Providers create resources and streams through
    /// these (Phase 4).
    /// </summary>
    ICapabilityFacilities? Facilities { get; }
}
