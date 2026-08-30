using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.dataset.describe@1</c> capability contract (plan §11
/// "schema description", §16 Phase 8, ADR-0028): describes one spatial
/// dataset — schema fields in column order (geometry fields included as
/// <c>Geometry</c>), the geometry column with SRID and geometry type, a
/// row-count estimate and the feature-identity columns. The output is a
/// bounded stream whose single item is the <c>dataset.description</c> JSON
/// document (<see cref="DatasetMetadataJson"/>). Dataset identities use the
/// <c>schema.table</c> grammar shared by every data contract.
/// </summary>
public static class DatasetDescribeContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.dataset.describe@1");

    /// <summary>The input interchange shape: a dataset identifier.</summary>
    public const string InputSchema = "dataset.identity";

    /// <summary>The output interchange shape: a dataset description JSON item.</summary>
    public const string OutputSchema = "dataset.description";

    /// <summary>A dataset name that is not a valid <c>schema.table</c>-ish identifier.</summary>
    private const string MalformedDataset = "not a valid dataset identifier: braces}";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Describes one dataset's schema: fields in column order, geometry column with SRID and geometry type, row estimate and feature-identity columns.",
        new SchemaDescriptor(InputSchema, "'dataset' — schema.table or table."),
        new SchemaDescriptor(OutputSchema, "A bounded stream whose one item is the dataset.description JSON document."),
        [new ErrorVariant("invalid.arguments", "The 'dataset' argument is missing, malformed or names a dataset that does not exist.")],
        [],
        CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
        [
            new ConformanceExample(
                "public-places",
                "Describing public.places yields its fields, geometry column, SRID and row estimate.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places")),
            new ConformanceExample(
                "underscored-schema",
                "A schema-qualified name with underscores is a valid dataset identifier.",
                ContractArguments.Build(ProviderArguments.Dataset, "spatial_data.roads_2024")),
            new ConformanceExample(
                "missing-dataset",
                "A describe without a dataset is an invalid argument.",
                ContractArguments.Build()),
            new ConformanceExample(
                "malformed-dataset",
                "A dataset name with illegal characters is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, MalformedDataset)),
        ]);
}
