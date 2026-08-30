using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared data-provider conformance fixtures (plan §18) against the
/// in-process PostGIS provider with an explicitly unconfigured store: the
/// argument-validation matrix, the redacted <c>provider.unavailable</c> shape
/// and the pre-cancelled <c>operation.cancelled</c> shape — the same
/// fixtures the isolated worker runs (<see cref="WorkerPostgisConformanceTests"/>).
/// </summary>
public sealed class InProcessPostgisConformanceTests
{
    private static readonly ProviderId ExpectedProvider = PostgisProvider.ProviderIdentifier;

    [Fact]
    public async Task The_postgis_provider_passes_the_data_provider_matrix()
    {
        var runtime = UnconfiguredRuntime();

        await PostgisProviderConformance.RunDataProviderMatrixAsync(InvokeAsync, ExpectedProvider);

        async Task<CapabilityOutcome> InvokeAsync(
            CapabilityId capability,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            await runtime.InvokeAsync(CapabilityInvocation.Create(capability, arguments) with
            {
                CancellationToken = cancellationToken,
                GrantedPermissions = new HashSet<Permission>
                {
                    Permission.Parse("spatial.feature.read"),
                    Permission.Parse("spatial.feature.write"),
                    Permission.Parse("spatial.dataset.create"),
                },
            });
    }

    [Fact]
    public void The_provider_registers_all_nine_data_contracts()
    {
        var provider = new PostgisProvider();
        var ids = provider.Descriptors.Select(descriptor => descriptor.Id.ToString()).OrderBy(id => id).ToArray();

        Assert.Equal(
            [
                "spatial.catalogue.list@1",
                "spatial.dataset.create@1",
                "spatial.dataset.describe@1",
                "spatial.feature.query@1",
                "spatial.feature.scan@1",
                "spatial.feature.write@1",
                "spatial.transaction.begin@1",
                "spatial.transaction.commit@1",
                "spatial.transaction.rollback@1",
            ],
            ids);
    }

    [Fact]
    public async Task An_unserved_capability_is_a_contract_violation()
    {
        var runtime = UnconfiguredRuntime();

        // Direct provider invocation: an unserved capability is a contract violation.
        var result = await new PostgisProvider(PostgisConnectionConfiguration.FromEnvironmentValue(null)).InvokeAsync(
            CapabilityInvocation.Create(Spatial.PluginSdk.Operations.BufferContract.Id, new Dictionary<string, object?>()));

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<CapabilityFailure>(result).Error;
        Assert.Equal(CapabilityErrorKind.ContractViolation, error.Kind);
        Assert.Contains("not served", error.Message);
    }

    private static CapabilityRuntime UnconfiguredRuntime()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new PostgisProvider(PostgisConnectionConfiguration.FromEnvironmentValue(null)));
        return new CapabilityRuntime(registry);
    }
}
