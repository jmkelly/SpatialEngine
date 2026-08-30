using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.feature.write@1</c> capability contract (plan §9/§11
/// "feature append", §16 Phase 8, ADR-0028): appends the features of a
/// canonical feature batch to a dataset. The <c>batch</c> argument is a
/// FeatureBatchCodec v1 byte array whose schema must be decodable from the
/// dataset's scan schema (append-only column evolution — the provider may
/// write a subset of the dataset's fields, never new ones), and the result
/// is the number of appended features. The write is a single transaction:
/// either every feature lands or none does. An optional <c>transaction</c>
/// handle (from <c>spatial.transaction.begin@1</c>) enlists the insert in an
/// open transaction instead — the caller then commits or rolls back. Every
/// value is bound as a parameter; nothing client-supplied is concatenated
/// into SQL (identifiers are validated against discovered tables/columns).
/// </summary>
public static class FeatureWriteContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.feature.write@1");

    /// <summary>The input interchange shape: dataset, canonical batch, optional transaction handle.</summary>
    public const string InputSchema = "feature.write";

    /// <summary>The output interchange shape: the appended feature count.</summary>
    public const string OutputSchema = "feature.count";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Appends the features of a canonical feature batch to a dataset (single-transaction insert), optionally enlisting in an open transaction.",
        new SchemaDescriptor(InputSchema, "'dataset'; 'batch' — canonical feature batch (FeatureBatchCodec v1 bytes); optional 'transaction' — a transaction handle."),
        new SchemaDescriptor(OutputSchema, "The number of appended features."),
        [new ErrorVariant("invalid.arguments", "The dataset id, batch bytes or transaction handle are missing, malformed or invalid; the batch schema is not decodable from the dataset's schema; or the transaction is not active.")],
        [Permission.Parse("spatial.feature.write")],
        CapabilityTraits.Cancellable | CapabilityTraits.SideEffects,
        [
            new ConformanceExample(
                "missing-batch",
                "A write without a batch is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places")),
            new ConformanceExample(
                "missing-dataset",
                "A write without a dataset is an invalid argument.",
                ContractArguments.Build()),
            new ConformanceExample(
                "malformed-dataset",
                "A dataset name with illegal characters is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "places]")),
            new ConformanceExample(
                "mistyped-transaction",
                "A transaction argument that is not a handle is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places", ProviderArguments.Batch, new byte[] { 1 }, ProviderArguments.Transaction, "tx-1")),
        ]);
}
