using Spatial.PluginSdk.Capabilities;

namespace Spatial.Plugin.Fixtures;

/// <summary>
/// Version 2 of the fixture provider (plan §10.4 side-by-side activation):
/// same provider name, next version, serving a subset of the same
/// capabilities. Version 2's <c>peek@1</c> returns <c>fixture@2</c>, so a
/// side-by-side test can observe which generation served an invocation.
/// </summary>
public sealed class FixtureProviderV2 : CapabilityProviderBase
{
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("fixture@2");

    public static readonly CapabilityId PeekCapability = CapabilityId.Parse("spatial.fixture.peek@1");
    public static readonly CapabilityId SleepCapability = CapabilityId.Parse("spatial.fixture.sleep@1");

    public FixtureProviderV2()
        : base(BuildDescriptors())
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        invocation.Capability == PeekCapability
            ? new ValueTask<CapabilityResult>(CapabilityResult.Success(ProviderIdentifier))
            : invocation.Capability == SleepCapability
                ? FixtureSleep.SleepFor(invocation, SleepCapability)
                : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
                    $"The fixture provider does not serve {invocation.Capability}.")));

    private static IReadOnlyList<CapabilityDescriptor> BuildDescriptors() =>
    [
        new CapabilityDescriptor(
            PeekCapability,
            "Returns the provider id that served the invocation.",
            new SchemaDescriptor("none"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.None,
            []),

        new CapabilityDescriptor(
            SleepCapability,
            "Sleeps for the requested milliseconds, reporting progress — cancellable.",
            new SchemaDescriptor("scalar"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
            []),
    ];
}
