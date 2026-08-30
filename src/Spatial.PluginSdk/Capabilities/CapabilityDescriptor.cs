namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The immutable declaration of one capability contract (plan §9): identity,
/// purpose, input and output schemas, error variants, required permissions,
/// runtime traits (side effects, streaming, cancellation), and conformance
/// examples. Descriptors are validated cross-cutting at registration: at
/// least one error variant, a purpose, named schemas, and the rule that
/// long-running capabilities are cancellable (ADR-0008).
/// </summary>
public sealed record CapabilityDescriptor(
    CapabilityId Id,
    string Purpose,
    SchemaDescriptor Input,
    SchemaDescriptor Output,
    IReadOnlyList<ErrorVariant> Errors,
    IReadOnlyList<Permission> RequiredPermissions,
    CapabilityTraits Traits,
    IReadOnlyList<ConformanceExample> Examples)
{
    public override string ToString() => $"{Id}: {Purpose}";
}