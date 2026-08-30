using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Deterministic provider resolution (plan §9): explicit caller pin →
/// compatible resource-local provider → configured preferred provider →
/// first healthy provider by stable id.
/// </summary>
public sealed class ResolutionTests
{
    private static readonly CapabilityId Scan = CapabilityId.Parse("spatial.feature.scan@1");

    [Fact]
    public void First_healthy_picks_the_lowest_provider_id()
    {
        var (_, runtime) = Create(new StubProvider("zeta", 1, Scan), new StubProvider("alpha", 1, Scan));

        var resolved = runtime.Resolve(Scan);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("alpha@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.FirstHealthy, resolved.Step);
    }

    [Fact]
    public void Configured_preference_beats_first_healthy()
    {
        var (_, runtime) = Create(
            CapabilityConfiguration.WithPreference(Scan, ProviderId.Parse("zeta@1")),
            new StubProvider("zeta", 1, Scan),
            new StubProvider("alpha", 1, Scan));

        var resolved = runtime.Resolve(Scan);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("zeta@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.ConfiguredPreferred, resolved.Step);
    }

    [Fact]
    public void Explicit_pin_beats_preference_and_resource_local()
    {
        var (_, runtime) = Create(
            CapabilityConfiguration.WithPreference(Scan, ProviderId.Parse("alpha@1")),
            new StubProvider("zeta", 1, Scan),
            new StubProvider("alpha", 1, Scan),
            new StubProvider("beta", 1, Scan));
        var options = new InvocationOptions(
            ExplicitProvider: ProviderId.Parse("beta@1"),
            ResourceLocalProvider: ProviderId.Parse("zeta@1"));

        var resolved = runtime.Resolve(Scan, options);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("beta@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.Explicit, resolved.Step);
    }

    [Fact]
    public void Resource_local_beats_configured_preference()
    {
        var (_, runtime) = Create(
            CapabilityConfiguration.WithPreference(Scan, ProviderId.Parse("alpha@1")),
            new StubProvider("zeta", 1, Scan),
            new StubProvider("alpha", 1, Scan));
        var options = new InvocationOptions(ResourceLocalProvider: ProviderId.Parse("zeta@1"));

        var resolved = runtime.Resolve(Scan, options);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("zeta@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.ResourceLocal, resolved.Step);
    }

    [Fact]
    public void Resource_local_that_cannot_serve_falls_through()
    {
        var (_, runtime) = Create(
            new StubProvider("alpha", 1, Scan),
            new StubProvider("local", 1, CapabilityId.Parse("spatial.feature.write@1")));
        var options = new InvocationOptions(ResourceLocalProvider: ProviderId.Parse("local@1"));

        var resolved = runtime.Resolve(Scan, options);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("alpha@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.FirstHealthy, resolved.Step);
    }

    [Fact]
    public void Explicit_pin_to_a_provider_that_does_not_serve_returns_null()
    {
        var (_, runtime) = Create(
            new StubProvider("alpha", 1, CapabilityId.Parse("spatial.feature.write@1")),
            new StubProvider("beta", 1, Scan));
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("alpha@1"));

        Assert.Null(runtime.Resolve(Scan, options));
    }

    [Fact]
    public void Explicit_pin_to_an_unregistered_provider_returns_null()
    {
        var (_, runtime) = Create(new StubProvider("beta", 1, Scan));
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("ghost@1"));

        Assert.Null(runtime.Resolve(Scan, options));
    }

    [Fact]
    public void Explicit_pin_to_an_unhealthy_provider_returns_null()
    {
        var (registry, runtime) = Create(new StubProvider("alpha", 1, Scan));
        registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Unhealthy);
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("alpha@1"));

        Assert.Null(runtime.Resolve(Scan, options));
    }

    [Fact]
    public void Configured_preference_to_an_unhealthy_provider_falls_back_to_first_healthy()
    {
        var (registry, runtime) = Create(
            CapabilityConfiguration.WithPreference(Scan, ProviderId.Parse("zeta@1")),
            new StubProvider("zeta", 1, Scan),
            new StubProvider("alpha", 1, Scan));
        registry.SetHealth(ProviderId.Parse("zeta@1"), ProviderHealth.Unhealthy);

        var resolved = runtime.Resolve(Scan);

        Assert.NotNull(resolved);
        Assert.Equal(ProviderId.Parse("alpha@1"), resolved.Provider.Id);
        Assert.Equal(ResolutionStep.FirstHealthy, resolved.Step);
    }

    [Fact]
    public void Unhealthy_providers_are_never_selected()
    {
        var (registry, runtime) = Create(
            new StubProvider("alpha", 1, Scan),
            new StubProvider("beta", 1, Scan));
        registry.SetHealth(ProviderId.Parse("beta@1"), ProviderHealth.Unhealthy);

        Assert.Equal(ProviderId.Parse("alpha@1"), runtime.Resolve(Scan)?.Provider.Id);

        registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Unhealthy);
        Assert.Null(runtime.Resolve(Scan));
    }

    [Fact]
    public void Degraded_providers_remain_usable()
    {
        var (registry, runtime) = Create(
            new StubProvider("alpha", 1, Scan),
            new StubProvider("beta", 1, Scan));
        registry.SetHealth(ProviderId.Parse("beta@1"), ProviderHealth.Degraded);

        Assert.Equal(ProviderId.Parse("alpha@1"), runtime.Resolve(Scan)?.Provider.Id);

        registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Unhealthy);
        Assert.Equal(ProviderId.Parse("beta@1"), runtime.Resolve(Scan)?.Provider.Id);
    }

    [Fact]
    public void No_provider_for_the_capability_returns_null()
    {
        var (_, runtime) = Create(new StubProvider("alpha", 1, Scan));

        Assert.Null(runtime.Resolve(CapabilityId.Parse("spatial.geometry.buffer@1")));
    }

    private static (CapabilityRegistry Registry, CapabilityRuntime Runtime) Create(
        params ICapabilityProvider[] providers) =>
        Create(CapabilityConfiguration.Empty, providers);

    private static (CapabilityRegistry Registry, CapabilityRuntime Runtime) Create(
        CapabilityConfiguration configuration,
        params ICapabilityProvider[] providers)
    {
        var registry = new CapabilityRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider);
        }

        return (registry, new CapabilityRuntime(registry, configuration));
    }
}