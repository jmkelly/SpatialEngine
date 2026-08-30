using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using Spatial.Operations.NetTopologySuite.Operations;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite operations provider (plan §16 Phase 6, Epic G): a
/// replaceable implementation of the standard geometry operation contracts
/// (ADR-0026 — buffer, intersection, validation and simplification) run on
/// NetTopologySuite. The provider is a plugin implementation: it implements
/// <c>Spatial.PluginSdk</c> only and is launched as an isolated worker by
/// the plugin host — the runtime or host never reference it (ADR-0006/0002).
/// Its entire public surface is contracts plus SDK types; every NTS type
/// lives inside the private adapters (ADR-0005).
/// </summary>
public sealed class NtsOperationsProvider : CapabilityProviderBase
{
    /// <summary>The stable provider identity (<c>nts@1</c>).</summary>
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("nts@1");

    private static readonly IReadOnlyList<CapabilityDescriptor> ContractDescriptors =
    [
        BufferContract.Descriptor,
        IntersectionContract.Descriptor,
        ValidateContract.Descriptor,
        SimplifyContract.Descriptor,
    ];

    public NtsOperationsProvider()
        : base(ContractDescriptors)
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        invocation.Capability == BufferContract.Id ? NtsBufferOperation.RunAsync(invocation)
        : invocation.Capability == IntersectionContract.Id ? NtsIntersectionOperation.RunAsync(invocation)
        : invocation.Capability == ValidateContract.Id ? NtsValidateOperation.RunAsync(invocation)
        : invocation.Capability == SimplifyContract.Id ? NtsSimplifyOperation.RunAsync(invocation)
        : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"The NetTopologySuite operations provider does not serve {invocation.Capability}.")));
}