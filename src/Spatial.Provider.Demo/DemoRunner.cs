using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;

namespace Spatial.Provider.Demo;

/// <summary>
/// The invocation surface of the <c>demo@1</c> provider (Phase 10,
/// ADR-0031): a thin capability dispatch table over the read-only data
/// contracts and the demo sleep. The handlers themselves live in focused
/// per-stream-shape types — <see cref="DemoCatalogueHandler"/> for the
/// dataset-metadata streams and <see cref="DemoFeatureHandler"/> for the
/// canonical feature batches — so the runner carries the dispatch only and
/// stays small, mirroring the PostGIS runner's shape.
/// </summary>
internal sealed class DemoRunner
{
    /// <summary>The resource kind of every demo stream the provider mints.</summary>
    internal static readonly ResourceKind StreamKind = ResourceKind.Parse("demo.stream");

    private readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> _handlers;

    public DemoRunner()
    {
        _handlers = new Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>>
        {
            [DemoCapabilities.CatalogueList] = DemoCatalogueHandler.CatalogueListAsync,
            [DemoCapabilities.DatasetDescribe] = DemoCatalogueHandler.DescribeAsync,
            [DemoCapabilities.FeatureScan] = DemoFeatureHandler.ScanAsync,
            [DemoCapabilities.FeatureQuery] = DemoFeatureHandler.QueryAsync,
            [DemoCapabilities.Sleep] = DemoSleep.SleepForAsync,
        };
    }

    public static IReadOnlyList<CapabilityDescriptor> Descriptors => DemoCapabilities.Descriptors;

    public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return _handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(
                CapabilityError.ContractViolation($"{invocation.Capability} is not served by demo@1.")));
    }
}
