using Spatial.Operations.NetTopologySuite.Operations;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The second released version of the NetTopologySuite operations provider
/// (<c>nts@2</c>): the same four standard geometry-operation contracts on
/// the same adapters, under a new provider identity — the vehicle the
/// browser workbench and the replacement demonstration use to show
/// side-by-side versions routing (plan §10.4, §17.9-11; ADR-0031). A real
/// new build of a plugin keeps its contract surface stable and changes only
/// its provider version; v2 reuses the shared operation runners verbatim so
/// the two versions can never disagree on a result (the shared conformance
/// suite pins both).
/// </summary>
public sealed class NtsOperationsProviderV2 : CapabilityProviderBase
{
    /// <summary>The stable provider identity (<c>nts@2</c>).</summary>
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("nts@2");

    private static readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> Handlers =
        new()
        {
            [BufferContract.Id] = NtsOperationRunner.BufferAsync,
            [IntersectionContract.Id] = NtsOperationRunner.IntersectionAsync,
            [ValidateContract.Id] = NtsOperationRunner.ValidateAsync,
            [SimplifyContract.Id] = NtsOperationRunner.SimplifyAsync,
        };

    /// <summary>The shared contract descriptors of the four standard operations.</summary>
    private static readonly IReadOnlyList<CapabilityDescriptor> ContractDescriptors =
    [
        BufferContract.Descriptor,
        IntersectionContract.Descriptor,
        ValidateContract.Descriptor,
        SimplifyContract.Descriptor,
    ];

    public NtsOperationsProviderV2()
        : base(ContractDescriptors)
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        Handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
                $"The NetTopologySuite operations provider v2 does not serve {invocation.Capability}.")));
}
