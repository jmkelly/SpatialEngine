using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Transformations.ProjNet;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures (plan §18) against the in-process ProjNet
/// transformation provider: the same success, empty-input, unsupported-input,
/// cancellation, diagnostics and provenance examples every provider of the
/// <c>spatial.coordinate.transform@1</c> and <c>spatial.crs.describe@1</c>
/// contracts must serve identically (<see cref="TransformConformance"/>).
/// </summary>
public sealed class InProcessTransformConformanceTests
{
    private static readonly ProviderId ExpectedProvider = ProjNetTransformationsProvider.ProviderIdentifier;

    [Fact]
    public async Task The_projnet_provider_passes_the_transformation_matrix()
    {
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        runtime.Registry.Register(new ProjNetTransformationsProvider());

        await TransformConformance.RunTransformationMatrixAsync(InvokeAsync, ExpectedProvider);

        async Task<CapabilityOutcome> InvokeAsync(
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
        runtime.Registry.Register(new ProjNetTransformationsProvider());

        await TransformConformance.AssertCancellationAsync(InvokeAsync, ExpectedProvider);

        async Task<CapabilityOutcome> InvokeAsync(
            CapabilityId capability,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            await runtime.InvokeAsync(CapabilityInvocation.Create(capability, arguments) with
            {
                CancellationToken = cancellationToken,
            });
    }
}
