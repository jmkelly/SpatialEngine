using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.transaction.commit@1</c> capability contract (plan §11,
/// §16 Phase 8, ADR-0028): commits an open transaction by its handle
/// (<c>spatial.transaction.begin@1</c>), making its enlisted writes durable,
/// and ends the handle. A commit on a handle that is no longer active (already
/// committed/rolled back, or the provider restarted) is an
/// <c>invalid.arguments</c> naming the dead transaction. The result is
/// <c>true</c>.
/// </summary>
public static class TransactionCommitContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.transaction.commit@1");

    /// <summary>The input interchange shape: a transaction handle.</summary>
    public const string InputSchema = "transaction.handle";

    /// <summary>The output interchange shape: a boolean success flag.</summary>
    public const string OutputSchema = "boolean";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Commits an open transaction by handle, making its enlisted writes durable.",
        new SchemaDescriptor(InputSchema, "'transaction' — a transaction handle from spatial.transaction.begin@1."),
        new SchemaDescriptor(OutputSchema, "true when the transaction committed."),
        [new ErrorVariant("invalid.arguments", "The 'transaction' argument is missing, is not a handle, or names a transaction that is no longer active.")],
        [],
        CapabilityTraits.Cancellable | CapabilityTraits.SideEffects,
        [
            new ConformanceExample(
                "missing-handle",
                "A commit without a transaction handle is an invalid argument.",
                ContractArguments.Build()),
        ]);
}
