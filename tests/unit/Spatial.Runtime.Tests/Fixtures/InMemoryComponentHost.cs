using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// The plan's in-memory component host (Epic D/E): wires a registry, the
/// configured provider preferences and the example capability provider into a
/// ready-to-use runtime — the in-process counterpart to the isolated worker
/// hosts of Phase 5. Tests exercise it end to end; Phase 4 tests reach the
/// runtime's resource and job registries through it.
/// </summary>
public sealed class InMemoryComponentHost
{
    internal InMemoryComponentHost(CapabilityRegistry registry, CapabilityRuntime runtime)
    {
        Registry = registry;
        Runtime = runtime;
    }

    public CapabilityRegistry Registry { get; }

    public CapabilityRuntime Runtime { get; }

    /// <summary>The runtime's resource tracker (opaque handles, leases, disposal, leak detection).</summary>
    public ResourceRegistry Resources => Runtime.Resources;

    /// <summary>The runtime's job tracker (long-running invocations, ADR-0008).</summary>
    public JobRegistry Jobs => Runtime.Jobs;

    public static InMemoryComponentHost Create(CapabilityConfiguration? configuration = null)
    {
        var registry = new CapabilityRegistry();
        registry.Register(new ExampleFeatureProvider());
        var runtime = new CapabilityRuntime(registry, configuration ?? CapabilityConfiguration.Empty);
        return new InMemoryComponentHost(registry, runtime);
    }
}
