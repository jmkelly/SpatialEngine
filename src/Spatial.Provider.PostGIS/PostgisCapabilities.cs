using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The <c>postgis@1</c> capability catalog (ADR-0028): the nine data-provider
/// contract descriptors the provider advertises and the capability ids its
/// dispatch table keys on. Keeping the catalog in one place means the
/// provider's invocation surface (<see cref="PostgisRunner"/>) depends on the
/// catalog, not on every individual contract.
/// </summary>
internal static class PostgisCapabilities
{
    /// <summary>The nine capability descriptors, one per data-provider contract.</summary>
    public static IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
    [
        CatalogueListContract.Descriptor,
        DatasetDescribeContract.Descriptor,
        DatasetCreateContract.Descriptor,
        FeatureScanContract.Descriptor,
        FeatureQueryContract.Descriptor,
        FeatureWriteContract.Descriptor,
        TransactionBeginContract.Descriptor,
        TransactionCommitContract.Descriptor,
        TransactionRollbackContract.Descriptor,
    ];

    public static CapabilityId CatalogueList => CatalogueListContract.Id;

    public static CapabilityId DatasetDescribe => DatasetDescribeContract.Id;

    public static CapabilityId DatasetCreate => DatasetCreateContract.Id;

    public static CapabilityId FeatureScan => FeatureScanContract.Id;

    public static CapabilityId FeatureQuery => FeatureQueryContract.Id;

    public static CapabilityId FeatureWrite => FeatureWriteContract.Id;

    public static CapabilityId TransactionBegin => TransactionBeginContract.Id;

    public static CapabilityId TransactionCommit => TransactionCommitContract.Id;

    public static CapabilityId TransactionRollback => TransactionRollbackContract.Id;
}
