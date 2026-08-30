using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using Spatial.Transformations.ProjNet.Operations;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The ProjNet coordinate transformation provider (plan §16 Phase 7, Epic G):
/// a replaceable implementation of the CRS description and coordinate
/// transformation contracts (ADR-0027 — <c>spatial.crs.describe@1</c> and
/// <c>spatial.coordinate.transform@1</c>) run on ProjNet 2.1 with an embedded
/// curated EPSG catalogue. The provider is a plugin implementation: it
/// implements <c>Spatial.PluginSdk</c> only and is launched as an isolated
/// worker by the plugin host — the runtime or host never reference it
/// (ADR-0006/0002). Its entire public surface is contracts plus SDK types;
/// every ProjNet type lives inside the private adapters and catalogue
/// (ADR-0005).
/// </summary>
public sealed class ProjNetTransformationsProvider : CapabilityProviderBase
{
    /// <summary>The stable provider identity (<c>projnet@1</c>).</summary>
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("projnet@1");

    private static readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> Handlers =
        new()
        {
            [CrsDescribeContract.Id] = ProjNetTransformRunner.DescribeAsync,
            [TransformContract.Id] = ProjNetTransformRunner.TransformAsync,
        };

    private static readonly IReadOnlyList<CapabilityDescriptor> ContractDescriptors =
    [
        CrsDescribeContract.Descriptor,
        TransformContract.Descriptor,
    ];

    public ProjNetTransformationsProvider()
        : base(ContractDescriptors)
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        Handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
                $"The ProjNet transformation provider does not serve {invocation.Capability}.")));
}
