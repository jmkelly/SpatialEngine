using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.catalogue.list@1</c> capability contract (plan §11/§16
/// Phase 8, ADR-0028): lists the provider's spatial datasets. Input is an
/// optional <c>pattern</c> (a dataset-name pattern with SQL LIKE wildcards);
/// the output is a bounded stream of <c>catalogue.metadata</c> items — one
/// per dataset — each a JSON document (<see cref="DatasetMetadataJson"/>)
/// carrying the dataset id, its geometry column, SRID and a row-count
/// estimate. Metadata is small, so it crosses as JSON text items (the plan's
/// "JSON for debugging and public API usability" rule); feature data never
/// does (ADR-0020). The embedded conformance examples are the DB-free
/// argument shapes; the store-backed examples ship with the integration
/// suite and the DB-free matrix with the shared conformance suite.
/// </summary>
public static class CatalogueListContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.catalogue.list@1");

    /// <summary>The input interchange shape: an optional dataset-name pattern.</summary>
    public const string InputSchema = "catalogue.filter";

    /// <summary>The output interchange shape: stream items of catalogue metadata JSON.</summary>
    public const string OutputSchema = "catalogue.metadata";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Lists the spatial datasets in the configured store: a bounded stream of catalogue.metadata JSON items (id, geometry column, SRID, row estimate).",
        new SchemaDescriptor(InputSchema, "Optional 'pattern' — a dataset-name pattern with SQL LIKE wildcards ('%' any run, '_' one character)."),
        new SchemaDescriptor(OutputSchema, "A bounded stream of catalogue.metadata JSON documents, one per dataset."),
        [new ErrorVariant("invalid.arguments", "The 'pattern' argument, when present, is not a string.")],
        [],
        CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
        [
            new ConformanceExample(
                "all-datasets",
                "Listing without a pattern returns every spatial dataset as a catalogue.metadata item.",
                ContractArguments.Build()),
            new ConformanceExample(
                "wildcard-pattern",
                "A pattern filters the listed datasets by name.",
                ContractArguments.Build(ProviderArguments.Pattern, "roads_%")),
            new ConformanceExample(
                "non-string-pattern",
                "A non-string pattern is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Pattern, 42)),
        ]);
}
