using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// A provider that exposes a caller-supplied descriptor list verbatim, so
/// tests can feed the registry invalid descriptors and assert its validation
/// rules (the plan's "descriptor validation" test vehicle).
/// </summary>
public sealed class RawProvider : ICapabilityProvider
{
    public RawProvider(string name, int version, IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        Id = new ProviderId(name, version);
        Descriptors = descriptors;
    }

    public ProviderId Id { get; }

    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

    public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        new(CapabilityResult.Success(Id));
}
