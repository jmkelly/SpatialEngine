using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.feature.scan@1</c> capability contract (plan §9/§11, §16
/// Phase 8, ADR-0028): reads every feature of a dataset as a bounded stream
/// of canonical feature batches. Each stream item is a FeatureBatchCodec v1
/// byte array (ADR-0020 — feature data crosses as canonical binary
/// interchange, never JSON); the batch schema is the dataset's scan schema in
/// column order, and the stream carries as many batches as the table needs.
/// The scan honours cancellation: a pre-cancelled invocation fails with
/// <c>operation.cancelled</c> before touching the store, and a cancellation
/// during the scan fails the stream.
/// </summary>
public static class FeatureScanContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.feature.scan@1");

    /// <summary>The input interchange shape: a dataset identifier.</summary>
    public const string InputSchema = "dataset.identity";

    /// <summary>The output interchange shape: canonical feature batches on a bounded stream.</summary>
    public const string OutputSchema = "feature.batch";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Streams every feature of a dataset as canonical feature batches (FeatureBatchCodec v1 bytes) on a bounded, cancellable stream.",
        new SchemaDescriptor(InputSchema, "'dataset' — schema.table or table."),
        new SchemaDescriptor(OutputSchema, "A bounded stream of canonical feature-batch byte arrays in scan order."),
        [new ErrorVariant("invalid.arguments", "The 'dataset' argument is missing, malformed or names a dataset that does not exist.")],
        [Permission.Parse("spatial.feature.read")],
        CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
        [
            new ConformanceExample(
                "public-places",
                "Scanning public.places streams its features in canonical feature batches.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places")),
            new ConformanceExample(
                "missing-dataset",
                "A scan without a dataset is an invalid argument.",
                ContractArguments.Build()),
            new ConformanceExample(
                "malformed-dataset",
                "A dataset name with illegal characters is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places;DROP TABLE places")),
        ]);
}
