using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// The in-memory component host end to end (Epic D "add an in-memory
/// component host and example capability"): core values travel through the
/// registry and runtime into the example provider and back.
/// </summary>
public sealed class InMemoryHostTests
{
    [Fact]
    public void Host_exposes_the_example_capabilities()
    {
        var host = InMemoryComponentHost.Create();

        Assert.Equal(
            [
                ExampleFeatureProvider.CountCapability,
                ExampleFeatureProvider.EnvelopeCapability,
                ExampleFeatureProvider.SleepCapability,
            ],
            host.Registry.Capabilities);
        Assert.Equal(1, host.Registry.ProviderCount);
        Assert.NotNull(host.Registry.GetProvider(ExampleFeatureProvider.ProviderIdentifier));
    }

    [Fact]
    public async Task Counts_a_core_feature_batch()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(
                ExampleFeatureProvider.CountCapability,
                new Dictionary<string, object?> { ["batch"] = FixtureBatches.Points((0, 0), (1, 1)) }));

        Assert.True(outcome.TryGetValue(out var value));
        var batch = Assert.IsType<FeatureBatch>(value);
        Assert.Equal(2, batch.Features[0].Attributes[0].Int64Value);
    }

    [Fact]
    public async Task Computes_the_envelope_of_points()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(
                ExampleFeatureProvider.EnvelopeCapability,
                new Dictionary<string, object?> { ["batch"] = FixtureBatches.Points((0, 0), (2, 0), (1, 3)) })
            with
            { GrantedPermissions = new HashSet<Permission> { ExampleFeatureProvider.ReadPermission } });

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        var batch = Assert.IsType<FeatureBatch>(value);
        var attributes = batch.Features[0].Attributes;
        Assert.Equal(0, attributes[0].DoubleValue);
        Assert.Equal(0, attributes[1].DoubleValue);
        Assert.Equal(2, attributes[2].DoubleValue);
        Assert.Equal(3, attributes[3].DoubleValue);
    }
}
