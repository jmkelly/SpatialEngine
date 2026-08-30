using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Phase 5 active preferences (plan §10.4 "route new work to the new
/// version"): the runtime's supervisor-driven preference table is consulted
/// between the resource-local step and the configured preference, so
/// side-by-side activation and rollback steer routing without touching the
/// immutable start-up configuration.
/// </summary>
public sealed class ActivePreferenceTests
{
    private static readonly CapabilityId Shared = CapabilityId.Parse("spatial.fixture.shared@1");

    [Fact]
    public async Task Active_preference_routes_new_work_to_the_selected_provider()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Shared));
        registry.Register(new StubProvider("beta", 1, Shared));
        var runtime = new CapabilityRuntime(registry);

        var first = await InvokeAsync(runtime);
        Assert.Equal("alpha@1", ResultProvider(first));
        Assert.Equal(ResolutionStep.FirstHealthy, first.Provenance.Step);

        runtime.SetActivePreference(Shared, ProviderId.Parse("beta@1"));
        var routed = await InvokeAsync(runtime);
        Assert.Equal("beta@1", ResultProvider(routed));
        Assert.Equal(ResolutionStep.ActivePreferred, routed.Provenance.Step);
        Assert.Equal(ProviderId.Parse("beta@1"), runtime.ActivePreferences[Shared]);
    }

    [Fact]
    public async Task Active_preference_falls_through_when_the_provider_cannot_serve()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Shared));
        registry.Register(new StubProvider("beta", 1, Shared));
        var runtime = new CapabilityRuntime(registry);

        runtime.SetActivePreference(Shared, ProviderId.Parse("beta@1"));
        Assert.True(registry.SetHealth(ProviderId.Parse("beta@1"), ProviderHealth.Unhealthy));

        var outcome = await InvokeAsync(runtime);
        Assert.Equal("alpha@1", ResultProvider(outcome));
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
    }

    [Fact]
    public async Task Clearing_the_active_preference_restores_default_routing()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Shared));
        registry.Register(new StubProvider("beta", 1, Shared));
        var runtime = new CapabilityRuntime(registry);

        runtime.SetActivePreference(Shared, ProviderId.Parse("beta@1"));
        Assert.Equal("beta@1", ResultProvider(await InvokeAsync(runtime)));

        runtime.ClearActivePreference(Shared);
        Assert.Equal("alpha@1", ResultProvider(await InvokeAsync(runtime)));
        Assert.Empty(runtime.ActivePreferences);
    }

    [Fact]
    public async Task Explicit_pinning_wins_over_the_active_preference()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Shared));
        registry.Register(new StubProvider("beta", 1, Shared));
        var runtime = new CapabilityRuntime(registry);

        runtime.SetActivePreference(Shared, ProviderId.Parse("beta@1"));
        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(Shared, new Dictionary<string, object?>()),
            new InvocationOptions(ExplicitProvider: ProviderId.Parse("alpha@1")));

        Assert.Equal("alpha@1", ResultProvider(outcome));
        Assert.Equal(ResolutionStep.Explicit, outcome.Provenance.Step);
    }

    private static async Task<CapabilityOutcome> InvokeAsync(CapabilityRuntime runtime) =>
        await runtime.InvokeAsync(CapabilityInvocation.Create(Shared, new Dictionary<string, object?>()));

    private static string? ResultProvider(CapabilityOutcome outcome) =>
        outcome.TryGetValue(out var value) && value is not null ? value.ToString() : null;
}
