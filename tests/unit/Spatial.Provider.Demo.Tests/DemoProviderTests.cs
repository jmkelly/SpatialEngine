using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// The provider's contract surface (Phase 10, ADR-0031): it must advertise
/// exactly the read-only data contracts plus the demo sleep, refuse anything
/// else, and never claim contracts it does not serve (writes, transactions).
/// </summary>
public sealed class DemoProviderTests
{
    [Fact]
    public void The_provider_registers_the_read_only_data_contracts_and_the_demo_sleep()
    {
        var provider = new DemoProvider();
        var ids = provider.Descriptors.Select(descriptor => descriptor.Id.ToString()).OrderBy(id => id).ToArray();

        Assert.Equal(
            [
                "spatial.catalogue.list@1",
                "spatial.dataset.describe@1",
                "spatial.demo.sleep@1",
                "spatial.feature.query@1",
                "spatial.feature.scan@1",
            ],
            ids);
    }

    [Fact]
    public void The_scan_contract_requires_feature_read_permission()
    {
        var provider = new DemoProvider();
        var scan = Assert.Single(provider.Descriptors, descriptor => descriptor.Id == FeatureScanContract.Id);
        Assert.Contains(Permission.Parse("spatial.feature.read"), scan.RequiredPermissions);
        Assert.True(scan.Traits.HasFlag(CapabilityTraits.Streaming));
    }

    [Fact]
    public async Task An_unserved_capability_is_not_found_through_the_runtime()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(CapabilityId.Parse("spatial.geometry.buffer@1"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.CapabilityNotFound, outcome.Error?.Kind);
        Assert.Contains("spatial.geometry.buffer@1", outcome.Error?.Message);
    }

    [Fact]
    public async Task The_provider_refuses_unserved_capabilities_directly()
    {
        var provider = new DemoProvider();

        var result = await provider.InvokeAsync(
            CapabilityInvocation.Create(CapabilityId.Parse("spatial.geometry.buffer@1"), new Dictionary<string, object?>()));

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<CapabilityFailure>(result).Error;
        Assert.Equal(CapabilityErrorKind.ContractViolation, error.Kind);
        Assert.Contains("not served", error.Message);
    }

    [Fact]
    public void The_provider_id_is_demo_at_one()
    {
        Assert.Equal("demo@1", DemoProvider.ProviderIdentifier.ToString());
    }
}
