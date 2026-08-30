using Spatial.PluginSdk.Capabilities;
using Spatial.Transformations.ProjNet;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures against the ProjNet transformation worker
/// as an isolated child process (ADR-0006/0025): a real worker runs the same
/// success, empty-input, unsupported-input, cancellation, diagnostics and
/// provenance examples, with geometry travelling the wire in the
/// canonical-binary <c>$geometry</c> tag (ADR-0020) and CRS descriptions in
/// the <c>$crs</c> tag (ADR-0027).
/// </summary>
public sealed class WorkerTransformConformanceTests : IAsyncLifetime
{
    private ProjNetWorkerConformanceRig? _rig;

    public async Task InitializeAsync() => _rig = await ProjNetWorkerConformanceRig.StartAsync();

    public async Task DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }
    }

    private static readonly ProviderId ExpectedProvider = ProjNetTransformationsProvider.ProviderIdentifier;

    [Fact]
    public async Task The_projnet_worker_passes_the_transformation_matrix()
    {
        await TransformConformance.RunTransformationMatrixAsync(InvokeAsync, ExpectedProvider);
    }

    [Fact]
    public async Task A_cancelled_invocation_is_a_cancelled_failure()
    {
        await TransformConformance.AssertCancellationAsync(InvokeAsync, ExpectedProvider);
    }

    private Task<Spatial.Runtime.Capabilities.CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        _rig!.InvokeAsync(capability, arguments, cancellationToken);
}
