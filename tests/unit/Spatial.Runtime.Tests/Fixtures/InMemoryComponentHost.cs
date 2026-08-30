using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// The plan's in-memory component host (Epic D): wires a registry, the
/// configured provider preferences and the example capability provider into a
/// ready-to-use runtime — the in-process counterpart to the isolated worker
/// hosts of Phase 5. Tests exercise it end to end.
/// </summary>
public sealed class InMemoryComponentHost
{
    private InMemoryComponentHost(CapabilityRegistry registry, CapabilityConfiguration configuration)
    {
        Registry = registry;
        Runtime = new CapabilityRuntime(registry, configuration);
    }

    public CapabilityRegistry Registry { get; }

    public CapabilityRuntime Runtime { get; }

    public static InMemoryComponentHost Create(CapabilityConfiguration? configuration = null)
    {
        var registry = new CapabilityRegistry();
        registry.Register(new ExampleFeatureProvider());
        return new InMemoryComponentHost(registry, configuration ?? CapabilityConfiguration.Empty);
    }
}