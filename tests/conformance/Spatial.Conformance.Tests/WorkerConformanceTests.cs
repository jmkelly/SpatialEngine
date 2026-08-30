using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures against the NetTopologySuite operations
/// worker as an isolated child process (ADR-0006/0025): a real worker runs
/// the same success, empty-input, unsupported-input, cancellation,
/// diagnostics and provenance examples, with geometry travelling the wire in
/// the canonical-binary <c>$geometry</c> tag (ADR-0020).
/// </summary>
public sealed class WorkerConformanceTests : IAsyncLifetime
{
    private NtsWorkerConformanceRig? _rig;

    public async Task InitializeAsync() => _rig = await NtsWorkerConformanceRig.StartAsync();

    public async Task DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }
    }

    private static readonly ProviderId ExpectedProvider = NtsOperationsProvider.ProviderIdentifier;

    [Fact]
    public async Task The_nts_worker_passes_the_operation_matrix()
    {
        await GeometryOperationConformance.RunOperationMatrixAsync(InvokeAsync, ExpectedProvider, skipWireIncompatible: true);
    }

    [Fact]
    public async Task A_cancelled_invocation_is_a_cancelled_failure()
    {
        await GeometryOperationConformance.AssertCancellationAsync(InvokeAsync, ExpectedProvider);
    }

    private Task<Spatial.Runtime.Capabilities.CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        _rig!.InvokeAsync(capability, arguments, cancellationToken);
}
