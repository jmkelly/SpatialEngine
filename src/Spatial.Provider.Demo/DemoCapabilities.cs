using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo;

/// <summary>
/// The <c>demo@1</c> capability catalog (Phase 10, ADR-0031): the standard
/// read-only data-provider contracts (catalogue, describe, scan and
/// bbox-only query — the demo provider never writes) plus one
/// long-running demo capability (<c>spatial.demo.sleep@1</c>) so the browser
/// workbench's progress panel has a real cancellable job without a database.
/// </summary>
internal static class DemoCapabilities
{
    /// <summary>The demo data contracts; descriptors live with the shared plugin SDK contracts.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
    [
        CatalogueListContract.Descriptor,
        DatasetDescribeContract.Descriptor,
        FeatureScanContract.Descriptor,
        FeatureQueryContract.Descriptor,
        DemoSleepContract.Descriptor,
    ];

    public static CapabilityId CatalogueList => CatalogueListContract.Id;

    public static CapabilityId DatasetDescribe => DatasetDescribeContract.Id;

    public static CapabilityId FeatureScan => FeatureScanContract.Id;

    public static CapabilityId FeatureQuery => FeatureQueryContract.Id;

    public static CapabilityId Sleep => DemoSleepContract.Id;
}

/// <summary>
/// The <c>spatial.demo.sleep@1</c> contract: sleeps for the requested
/// milliseconds reporting progress — the progress panel's long-running job.
/// A demo-only capability, so its descriptor lives here rather than in the
/// standard contract set.
/// </summary>
internal static class DemoSleepContract
{
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.demo.sleep@1");

    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Sleeps for the requested milliseconds, reporting progress — the demo job for the workbench's progress panel.",
        new SchemaDescriptor("scalar", "int64 'milliseconds' (default 300)."),
        new SchemaDescriptor("scalar", "The milliseconds slept."),
        [new ErrorVariant("invalid.arguments", "The 'milliseconds' argument is not a non-negative int64.")],
        [],
        CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
        []);
}
