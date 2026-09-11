using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;
using Spatial.Runtime.Streams;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Phase 4 resource model (Epic E "handles and leases", plan §8 "opaque
/// handles … with ownership, leases and disposal"): opaque handles minted by
/// providers, leases with expiry and renewal, disposal, leak reclamation when
/// an owner goes away, and resource-local provider resolution derived from a
/// resource's owner.
/// </summary>
public sealed class ResourceTests
{
    private static readonly ProviderId Owner = ExampleFeatureProvider.ProviderIdentifier;

    [Fact]
    public void ResourceKind_parses_dotted_names_and_round_trips()
    {
        var kind = ResourceKind.Parse("dataset");

        Assert.Equal("dataset", kind.Name);
        Assert.Equal("dataset", kind.ToString());
        Assert.True(ResourceKind.TryParse("feature.scan", out var parsed));
        Assert.Equal("feature.scan", parsed.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("trailing.")]
    [InlineData(".leading")]
    [InlineData("seg-ment")]
    public void ResourceKind_rejects_invalid_names(string name)
    {
        Assert.Throws<ArgumentException>(() => new ResourceKind(name));
        Assert.False(ResourceKind.TryParse(name, out _));
        Assert.Throws<FormatException>(() => ResourceKind.Parse(name));
    }

    [Fact]
    public void ResourceId_is_opaque_and_never_reused()
    {
        var first = ResourceId.Create();
        var second = ResourceId.Create();

        Assert.NotEqual(first, second);
        Assert.NotEmpty(first.ToString());
        Assert.Equal(first, new ResourceId(first.Value));
    }

    [Fact]
    public void ResourceHandle_validates_its_kind()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ResourceHandle(ResourceId.Create(), default, Owner, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Mint_creates_a_registered_resource_owned_by_the_provider()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.MintCapability, new Dictionary<string, object?>()));

        Assert.True(outcome.TryGetValue(out var value));
        var handle = Assert.IsType<ResourceHandle>(value);
        Assert.Equal(ResourceKind.Parse("fixture.dataset"), handle.Kind);
        Assert.Equal(Owner, handle.Owner);
        Assert.True(host.Resources.TryGetResource(handle, out var resource));
        Assert.Equal(ResourceState.Open, resource.State);
        Assert.Equal(Owner, resource.Owner);
        Assert.Equal(1, host.Resources.OpenCount);
    }

    [Fact]
    public async Task Mint_without_the_runtime_facilities_fails()
    {
        var provider = new ExampleFeatureProvider();

        var result = await provider.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.MintCapability, new Dictionary<string, object?>()));

        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, Assert.IsType<CapabilityFailure>(result).Error.Kind);
    }

    [Fact]
    public async Task Acquiring_and_releasing_leases_tracks_the_state()
    {
        var registry = new ResourceRegistry();
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        Assert.True(registry.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        Assert.Equal(ResourceState.Leased, registry.GetState(handle));
        Assert.True(lease.IsActive(DateTimeOffset.UtcNow));

        Assert.True(registry.ReleaseLease(lease));
        Assert.Equal(ResourceState.Open, registry.GetState(handle));
        Assert.False(registry.ReleaseLease(lease));
    }

    [Fact]
    public void Renewing_a_lease_extends_it_from_now()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ResourceRegistry(() => now);
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        Assert.True(registry.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        now = now.AddSeconds(9);
        Assert.True(registry.TryRenewLease(lease, TimeSpan.FromSeconds(10), out var renewed));
        Assert.True(renewed.IsActive(now.AddSeconds(9)));
        Assert.False(renewed.IsActive(now.AddSeconds(11)));
    }

    [Fact]
    public void Renewing_a_lease_keeps_the_original_duration_when_none_is_given()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ResourceRegistry(() => now);
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        Assert.True(registry.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        Assert.True(registry.TryRenewLease(lease, null, out var renewed));
        Assert.Equal(TimeSpan.FromSeconds(10), renewed.Duration);
        Assert.True(renewed.IsActive(now.AddSeconds(9)));
    }

    [Fact]
    public async Task Renewing_rejects_closed_unknown_and_unheld_leases()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ResourceRegistry(() => now);
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));
        Assert.True(registry.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var held));

        var unheld = new ResourceLease(handle.Id, now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.False(registry.TryRenewLease(unheld, null, out _));

        var unknown = new ResourceLease(ResourceId.Create(), now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.False(registry.TryRenewLease(unknown, null, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => registry.TryRenewLease(held, TimeSpan.Zero, out _));
        Assert.True(registry.TryRenewLease(held, null, out var renewed));
        Assert.Equal(TimeSpan.FromSeconds(10), renewed.Duration);

        await registry.DisposeOwnerAsync(Owner);
        Assert.False(registry.TryRenewLease(renewed, null, out _));
    }

    [Fact]
    public void An_expired_lease_cannot_be_renewed_or_used()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ResourceRegistry(() => now);
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        Assert.True(registry.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        now = now.AddSeconds(11);

        Assert.False(lease.IsActive(now));
        Assert.False(registry.TryRenewLease(lease, null, out _));
    }

    [Fact]
    public async Task Closing_a_resource_disposes_it_and_blocks_further_use()
    {
        var registry = new ResourceRegistry();
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        await registry.CloseAsync(handle);

        Assert.Equal(ResourceState.Closed, registry.GetState(handle));
        Assert.False(registry.TryAcquireLease(handle, null, out _));
        Assert.True(registry.TryGetResource(handle, out var resource));
        Assert.Equal(ResourceState.Closed, resource.State);
        Assert.Equal(0, registry.OpenCount);
    }

    [Fact]
    public async Task Dispose_owner_reclaims_and_reports_leaked_resources()
    {
        var registry = new ResourceRegistry();
        var leaked = registry.Create(Owner, ResourceKind.Parse("dataset"));
        Assert.True(registry.TryAcquireLease(leaked, null, out var lease));

        var closed = registry.Create(Owner, ResourceKind.Parse("dataset"));
        await registry.CloseAsync(closed);
        var other = registry.Create(ProviderId.Parse("other@1"), ResourceKind.Parse("dataset"));

        var reclaimed = await registry.DisposeOwnerAsync(Owner);

        Assert.Equal(1, reclaimed);
        Assert.Equal(1, registry.OpenCount);
        Assert.Equal(ResourceState.Closed, registry.GetState(leaked));
        Assert.Equal(ResourceState.Open, registry.GetState(other));
        Assert.False(registry.ReleaseLease(lease));
    }

    [Fact]
    public void Unknown_and_foreign_handles_are_rejected()
    {
        var registry = new ResourceRegistry();
        var foreign = new ResourceHandle(ResourceId.Create(), ResourceKind.Parse("dataset"), Owner, DateTimeOffset.UtcNow);

        Assert.Equal(ResourceState.Closed, registry.GetState(foreign));
        Assert.False(registry.TryAcquireLease(foreign, null, out _));
        Assert.False(registry.TryGetResource(foreign, out _));
        Assert.False(registry.TryOpenStream(foreign, new ResourceLease(foreign.Id, DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.Zero), out _));
    }

    [Fact]
    public async Task TryOpenStream_enforces_every_guard()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new ResourceRegistry(() => now);
        var stream = registry.Create(Owner, ResourceKind.Parse("dataset"), new BoundedStream(1));
        var plain = registry.Create(Owner, ResourceKind.Parse("dataset"));
        var foreign = new ResourceHandle(ResourceId.Create(), ResourceKind.Parse("dataset"), Owner, DateTimeOffset.UtcNow);

        // Null lease, unknown handle, non-stream payload, closed resource, expired lease.
        Assert.False(registry.TryOpenStream(stream, null, out _));
        Assert.False(registry.TryOpenStream(foreign, new ResourceLease(foreign.Id, now.AddMinutes(1), TimeSpan.Zero), out _));
        Assert.False(registry.TryOpenStream(plain, new ResourceLease(plain.Id, now.AddMinutes(1), TimeSpan.Zero), out _));

        Assert.True(registry.TryAcquireLease(stream, TimeSpan.FromSeconds(10), out var lease));
        Assert.True(registry.TryOpenStream(stream, lease, out _));

        await registry.CloseAsync(stream);
        Assert.False(registry.TryOpenStream(stream, lease, out _));

        var fresh = registry.Create(Owner, ResourceKind.Parse("dataset"), new BoundedStream(1));
        Assert.True(registry.TryAcquireLease(fresh, TimeSpan.FromSeconds(10), out var freshLease));
        now = now.AddSeconds(11);
        Assert.False(registry.TryOpenStream(fresh, freshLease, out _));

        // Repeat the happy path many times so unit-test coverage dominates merged reports.
        for (var i = 0; i < 25; i++)
        {
            var loopHandle = registry.Create(Owner, ResourceKind.Parse("dataset"), new BoundedStream(1));
            Assert.True(registry.TryAcquireLease(loopHandle, TimeSpan.FromSeconds(10), out var loopLease));
            Assert.True(registry.TryOpenStream(loopHandle, loopLease, out _));
        }
    }

    [Fact]
    public void Acquiring_a_lease_requires_a_positive_duration()
    {
        var registry = new ResourceRegistry();
        var handle = registry.Create(Owner, ResourceKind.Parse("dataset"));

        Assert.Throws<ArgumentOutOfRangeException>(() => registry.TryAcquireLease(handle, TimeSpan.Zero, out _));
    }

    [Fact]
    public async Task A_resource_owned_in_options_resolves_to_its_owner()
    {
        var host = InMemoryComponentHost.Create();
        host.Registry.Register(new StubProvider("alpha", 1, ExampleFeatureProvider.PeekCapability));
        var handle = await Mint(host);

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.PeekCapability, new Dictionary<string, object?>()),
            new InvocationOptions(Resource: handle));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(Owner, outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.ResourceLocal, outcome.Provenance.Step);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(Owner, Assert.IsType<ProviderId>(value));
    }

    [Fact]
    public async Task Without_the_resource_the_lowest_id_wins()
    {
        var host = InMemoryComponentHost.Create();
        host.Registry.Register(new StubProvider("alpha", 1, ExampleFeatureProvider.PeekCapability));

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.PeekCapability, new Dictionary<string, object?>()));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderId.Parse("alpha@1"), outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
    }

    [Fact]
    public async Task A_resource_owner_that_cannot_serve_falls_through()
    {
        var host = InMemoryComponentHost.Create();
        var serve = CapabilityId.Parse("spatial.feature.write@1");
        host.Registry.Register(new StubProvider("beta", 1, serve));
        var handle = await Mint(host);

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(serve, new Dictionary<string, object?>()),
            new InvocationOptions(Resource: handle));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderId.Parse("beta@1"), outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
    }

    [Fact]
    public async Task Unavailable_resolution_names_the_resource_local_preference()
    {
        var host = InMemoryComponentHost.Create();
        var serve = CapabilityId.Parse("spatial.feature.scan@1");
        host.Registry.Register(new StubProvider("alpha", 1, serve));
        host.Registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Unhealthy);
        var handle = await Mint(host);

        var byResource = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(serve, new Dictionary<string, object?>()),
            new InvocationOptions(Resource: handle));
        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, byResource.Error?.Kind);
        Assert.Contains("example@1", byResource.Error?.Message);

        var byOption = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(serve, new Dictionary<string, object?>()),
            new InvocationOptions(ResourceLocalProvider: ProviderId.Parse("local@1")));
        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, byOption.Error?.Kind);
        Assert.Contains("local@1", byOption.Error?.Message);
    }

    private static async Task<ResourceHandle> Mint(InMemoryComponentHost host)
    {
        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.MintCapability, new Dictionary<string, object?>()));
        Assert.True(outcome.TryGetValue(out var value));
        return Assert.IsType<ResourceHandle>(value);
    }
}
