using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.dataset.create@1</c> capability contract (plan §11
/// "result-table creation", §16 Phase 8, ADR-0028): creates a new feature
/// table in the configured store from a defining feature batch. The batch
/// argument is a canonical feature batch (FeatureBatchCodec v1 bytes) whose
/// schema defines the table: each field becomes a column (typed by
/// attribute kind) and geometry fields become a PostGIS geometry column at
/// the requested SRID (default 4326). The result is the created dataset id.
/// The batch bytes are canonical binary interchange — the required wire form
/// of feature data between components (ADR-0020). Clients encode with
/// <c>FeatureBatchCodec.Encode</c>; the provider decodes and validates the
/// schema before creating anything.
/// </summary>
public static class DatasetCreateContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.dataset.create@1");

    /// <summary>The input interchange shape: dataset identity, a defining canonical batch, an SRID.</summary>
    public const string InputSchema = "dataset.definition";

    /// <summary>The output interchange shape: the created dataset id.</summary>
    public const string OutputSchema = "dataset.identity";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Creates a feature table from a defining feature batch: fields become typed columns, geometry fields a PostGIS geometry column at the requested SRID.",
        new SchemaDescriptor(InputSchema, "'dataset' — the new schema.table or table; 'batch' — a canonical feature batch (FeatureBatchCodec v1 bytes) whose schema defines the table; optional 'srid' (int, default 4326)."),
        new SchemaDescriptor(OutputSchema, "The id of the created dataset."),
        [new ErrorVariant("invalid.arguments", "The dataset id, batch bytes or SRID are missing, malformed or invalid; or the dataset already exists.")],
        [Permission.Parse("spatial.dataset.create")],
        CapabilityTraits.Cancellable | CapabilityTraits.SideEffects,
        [
            new ConformanceExample(
                "missing-batch",
                "A create without a defining batch is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.result")),
            new ConformanceExample(
                "missing-dataset",
                "A create without a dataset id is an invalid argument.",
                ContractArguments.Build()),
            new ConformanceExample(
                "non-string-dataset",
                "A non-string dataset id is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, 7)),
        ]);
}
