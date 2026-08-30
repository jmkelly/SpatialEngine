using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// A controllable provider for resolution and routing tests: serves the given
/// capability ids with indistinguishable stub descriptors and (optionally) a
/// custom invocation handler for failure-path tests.
/// </summary>
public sealed class StubProvider : CapabilityProviderBase
{
    private readonly Func<CapabilityInvocation, ValueTask<CapabilityResult>> _handler;

    public StubProvider(
        string name,
        int version,
        IReadOnlyList<CapabilityId> capabilities,
        Func<CapabilityInvocation, ValueTask<CapabilityResult>>? handler = null)
        : base(capabilities.Select(StubDescriptor).ToArray())
    {
        Id = new ProviderId(name, version);
        _handler = handler
            ?? (invocation => new ValueTask<CapabilityResult>(CapabilityResult.Success(Id)));
    }

    public StubProvider(string name, int version, CapabilityId capability)
        : this(name, version, [capability])
    {
    }

    public override ProviderId Id { get; }

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        _handler(invocation);

    private static CapabilityDescriptor StubDescriptor(CapabilityId capability) =>
        new(
            capability,
            $"Stub serving {capability}.",
            new SchemaDescriptor("stub.in"),
            new SchemaDescriptor("stub.out"),
            [new ErrorVariant("stub.error", "A stub capability error.")],
            [],
            CapabilityTraits.Cancellable,
            []);
}
