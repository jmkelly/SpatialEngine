using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures (plan §18) against the in-process
/// NetTopologySuite provider: the same success, empty-input, unsupported-input,
/// cancellation, diagnostics and provenance examples every provider of the
/// buffer/intersection/validate/simplify contracts must serve identically
/// (<see cref="GeometryOperationConformance"/>).
/// </summary>
public sealed class InProcessConformanceTests
{
    private static readonly ProviderId ExpectedProvider = NtsOperationsProvider.ProviderIdentifier;

    [Fact]
    public async Task The_nts_operations_provider_passes_the_operation_matrix()
    {
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        runtime.Registry.Register(new NtsOperationsProvider());

        await GeometryOperationConformance.RunOperationMatrixAsync(InvokeAsync, ExpectedProvider);

        async Task<Spatial.Runtime.Capabilities.CapabilityOutcome> InvokeAsync(
            CapabilityId capability,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            await runtime.InvokeAsync(CapabilityInvocation.Create(capability, arguments) with
            {
                CancellationToken = cancellationToken,
            });
    }

    [Fact]
    public async Task A_cancelled_invocation_is_a_cancelled_failure()
    {
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        runtime.Registry.Register(new NtsOperationsProvider());

        await GeometryOperationConformance.AssertCancellationAsync(InvokeAsync, ExpectedProvider);

        async Task<Spatial.Runtime.Capabilities.CapabilityOutcome> InvokeAsync(
            CapabilityId capability,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            await runtime.InvokeAsync(CapabilityInvocation.Create(capability, arguments) with
            {
                CancellationToken = cancellationToken,
            });
    }
}
