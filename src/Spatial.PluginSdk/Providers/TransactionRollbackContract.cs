using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.transaction.rollback@1</c> capability contract (plan §11,
/// §16 Phase 8, ADR-0028): rolls back an open transaction by its handle
/// (<c>spatial.transaction.begin@1</c>), discarding its enlisted writes, and
/// ends the handle. A rollback on a handle that is no longer active is an
/// <c>invalid.arguments</c> naming the dead transaction. The result is
/// <c>true</c>.
/// </summary>
public static class TransactionRollbackContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.transaction.rollback@1");

    /// <summary>The input interchange shape: a transaction handle.</summary>
    public const string InputSchema = "transaction.handle";

    /// <summary>The output interchange shape: a boolean success flag.</summary>
    public const string OutputSchema = "boolean";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Rolls back an open transaction by handle, discarding its enlisted writes.",
        new SchemaDescriptor(InputSchema, "'transaction' — a transaction handle from spatial.transaction.begin@1."),
        new SchemaDescriptor(OutputSchema, "true when the transaction rolled back."),
        [new ErrorVariant("invalid.arguments", "The 'transaction' argument is missing, is not a handle, or names a transaction that is no longer active.")],
        [],
        CapabilityTraits.Cancellable | CapabilityTraits.SideEffects,
        [
            new ConformanceExample(
                "missing-handle",
                "A rollback without a transaction handle is an invalid argument.",
                ContractArguments.Build()),
        ]);
}
