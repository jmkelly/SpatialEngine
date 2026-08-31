using Spatial.PluginSdk.Capabilities;

namespace Spatial.Provider.Demo;

/// <summary>
/// The <c>demo@1</c> provider (Phase 10, ADR-0031): a replaceable read-only
/// implementation of the standard data-provider contracts over procedurally
/// generated point datasets — the Docker-free dataset-browser and map
/// vehicle for the browser workbench and its Playwright tests. The provider
/// is a plugin implementation: it implements <c>Spatial.PluginSdk</c> only
/// and is launched as an isolated worker by the plugin host — the runtime or
/// host never reference it (ADR-0006/0002). Its entire public surface is
/// contracts plus SDK types.
/// </summary>
public sealed class DemoProvider : CapabilityProviderBase
{
    /// <summary>The stable provider identity (<c>demo@1</c>).</summary>
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("demo@1");

    private readonly DemoRunner _runner;

    public DemoProvider()
        : base(DemoRunner.Descriptors)
    {
        _runner = new DemoRunner();
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) => _runner.InvokeAsync(invocation);
}
