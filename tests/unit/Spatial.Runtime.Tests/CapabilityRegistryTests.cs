using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Capability registry behaviour: registration, descriptor validation
/// (plan §9), unregistration, health tracking and capability indexing.
/// </summary>
public sealed class CapabilityRegistryTests
{
    private static readonly CapabilityId Scan = CapabilityId.Parse("spatial.feature.scan@1");
    private static readonly CapabilityId Query = CapabilityId.Parse("spatial.feature.query@1");

    [Fact]
    public void Register_adds_provider_and_its_capabilities()
    {
        var registry = new CapabilityRegistry();
        var provider = new StubProvider("alpha", 1, [Scan, Query]);

        registry.Register(provider);

        Assert.Equal(1, registry.ProviderCount);
        Assert.Same(provider, registry.GetProvider(ProviderId.Parse("alpha@1"))?.Provider);
        Assert.Equal(ProviderHealth.Healthy, registry.GetProvider(ProviderId.Parse("alpha@1"))?.Health);
        Assert.Equal([Query, Scan], registry.Capabilities);
        Assert.Single(registry.GetProviders(Scan));
    }

    [Fact]
    public void Register_same_provider_twice_throws()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Scan));

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new StubProvider("alpha", 1, Scan)));

        Assert.Contains("alpha@1", exception.Message);
        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void Register_provider_with_duplicate_capability_throws()
    {
        var registry = new CapabilityRegistry();

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new StubProvider("alpha", 1, [Scan, Scan])));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void Register_provider_without_capabilities_throws()
    {
        var registry = new CapabilityRegistry();

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new StubProvider("alpha", 1, [])));

        Assert.Contains("declares no capabilities", exception.Message);
    }

    [Fact]
    public void Register_descriptor_without_purpose_throws()
    {
        var registry = new CapabilityRegistry();

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new RawProvider("alpha", 1, [Descriptor(Scan, purpose: "")])));

        Assert.Contains("must declare a purpose", exception.Message);
    }

    [Fact]
    public void Register_descriptor_without_error_variants_throws()
    {
        var registry = new CapabilityRegistry();

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new RawProvider("alpha", 1, [Descriptor(Scan, errors: [])])));

        Assert.Contains("at least one error variant", exception.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Register_descriptor_with_blank_schema_name_throws(bool blankInput, bool blankOutput)
    {
        var registry = new CapabilityRegistry();
        var descriptor = Descriptor(
            Scan,
            input: blankInput ? new SchemaDescriptor("") : new SchemaDescriptor("in"),
            output: blankOutput ? new SchemaDescriptor("") : new SchemaDescriptor("out"));

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new RawProvider("alpha", 1, [descriptor])));

        Assert.Contains("schema", exception.Message);
    }

    [Fact]
    public void Register_descriptor_without_permission_declaration_throws()
    {
        var registry = new CapabilityRegistry();
        var descriptor = new CapabilityDescriptor(
            Scan,
            "Without permissions.",
            new SchemaDescriptor("in"),
            new SchemaDescriptor("out"),
            [new ErrorVariant("test.error", "A test error.")],
            null!,
            CapabilityTraits.Cancellable,
            []);

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new RawProvider("alpha", 1, [descriptor])));

        Assert.Contains("required permissions", exception.Message);
    }

    [Fact]
    public void Register_long_running_descriptor_that_is_not_cancellable_throws()
    {
        var registry = new CapabilityRegistry();

        var exception = Assert.Throws<CapabilityRegistrationException>(
            () => registry.Register(new RawProvider("alpha", 1, [Descriptor(Scan, traits: CapabilityTraits.LongRunning)])));

        Assert.Contains("long-running", exception.Message);
        Assert.Contains("cancellable", exception.Message);
    }

    [Fact]
    public void Two_providers_can_serve_the_same_capability()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Scan));
        registry.Register(new StubProvider("beta", 1, Scan));

        var providers = registry.GetProviders(Scan);

        Assert.Equal(2, providers.Count);
        Assert.Equal(ProviderId.Parse("alpha@1"), providers[0].Id);
        Assert.Equal(ProviderId.Parse("beta@1"), providers[1].Id);
    }

    [Fact]
    public void Unregister_removes_provider_and_its_capability_index()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Scan));

        Assert.True(registry.Unregister(ProviderId.Parse("alpha@1")));
        Assert.Equal(0, registry.ProviderCount);
        Assert.Empty(registry.GetProviders(Scan));
        Assert.Empty(registry.Capabilities);
        Assert.False(registry.Unregister(ProviderId.Parse("alpha@1")));
    }

    [Fact]
    public void SetHealth_updates_health_and_rejects_unknown_ids_and_values()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("alpha", 1, Scan));

        Assert.True(registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Degraded));
        Assert.Equal(ProviderHealth.Degraded, registry.GetProvider(ProviderId.Parse("alpha@1"))?.Health);

        Assert.False(registry.SetHealth(ProviderId.Parse("missing@1"), ProviderHealth.Unhealthy));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.SetHealth(ProviderId.Parse("alpha@1"), (ProviderHealth)99));
    }

    [Fact]
    public void Providers_are_listed_in_stable_order()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new StubProvider("zeta", 1, Scan));
        registry.Register(new StubProvider("alpha", 1, Scan));

        Assert.Equal(["alpha@1", "zeta@1"], registry.Providers.Select(p => p.Id.ToString()));
    }

    private static CapabilityDescriptor Descriptor(
        CapabilityId id,
        string? purpose = null,
        SchemaDescriptor? input = null,
        SchemaDescriptor? output = null,
        IReadOnlyList<ErrorVariant>? errors = null,
        IReadOnlyList<Permission>? permissions = null,
        CapabilityTraits traits = CapabilityTraits.Cancellable) =>
        new(
            id,
            purpose ?? "Tests a capability.",
            input ?? new SchemaDescriptor("in"),
            output ?? new SchemaDescriptor("out"),
            errors ?? [new ErrorVariant("test.error", "A test error.")],
            permissions ?? [],
            traits,
            []);
}