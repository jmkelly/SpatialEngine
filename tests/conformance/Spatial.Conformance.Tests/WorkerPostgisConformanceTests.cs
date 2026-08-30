using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared data-provider conformance fixtures against the PostGIS provider
/// as an isolated worker process (ADR-0006/0025): a packaged <c>postgis@1</c>
/// worker launched with no connection configuration runs the same
/// argument-validation, unavailable-diagnostics and cancellation examples as
/// the in-process provider.
/// </summary>
public sealed class WorkerPostgisConformanceTests : IAsyncLifetime
{
    private PostgisWorkerConformanceRig? _rig;

    public async Task InitializeAsync() => _rig = await PostgisWorkerConformanceRig.StartAsync();

    public async Task DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }
    }

    private static readonly ProviderId ExpectedProvider = PostgisProvider.ProviderIdentifier;

    [Fact]
    public async Task The_postgis_worker_passes_the_data_provider_matrix()
    {
        await PostgisProviderConformance.RunDataProviderMatrixAsync(InvokeAsync, ExpectedProvider, skipWireIncompatible: true);
    }

    private Task<Spatial.Runtime.Capabilities.CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        _rig!.InvokeAsync(capability, arguments, cancellationToken);
}
