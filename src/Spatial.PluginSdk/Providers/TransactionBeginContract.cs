using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.transaction.begin@1</c> capability contract (plan §11,
/// §16 Phase 8, ADR-0028): opens a database transaction in the provider and
/// returns its runtime-owned handle (<c>$resource</c>, kind
/// <c>transaction</c>, ADR-0022/0025). Writes accepting a
/// <c>transaction</c> argument enlist in it; the caller ends it with
/// <c>spatial.transaction.commit@1</c> or
/// <c>spatial.transaction.rollback@1</c>. The handle maps to live provider
/// state: after a commit/rollback (or a provider restart) the handle is no
/// longer usable, and commit/rollback on an inactive handle is an
/// <c>invalid.arguments</c> naming the dead transaction. No arguments.
/// </summary>
public static class TransactionBeginContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.transaction.begin@1");

    /// <summary>The input interchange shape: none.</summary>
    public const string InputSchema = "none";

    /// <summary>The output interchange shape: a transaction handle.</summary>
    public const string OutputSchema = "transaction.handle";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Opens a provider-side database transaction and returns its runtime-owned handle.",
        new SchemaDescriptor(InputSchema, "No arguments."),
        new SchemaDescriptor(OutputSchema, "A $resource transaction handle owned by the provider."),
        [new ErrorVariant("provider.unavailable", "The provider has no usable connection configuration.")],
        [],
        CapabilityTraits.Cancellable | CapabilityTraits.SideEffects,
        [
            new ConformanceExample("begin", "Begins a transaction and returns its handle.", ContractArguments.Build()),
        ]);
}
