namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// Convenience base for capability providers: owns the descriptor list and
/// offers a descriptor lookup by capability id. Providers override
/// <see cref="Id"/> and <see cref="InvokeAsync"/>.
/// </summary>
public abstract class CapabilityProviderBase : ICapabilityProvider
{
    private readonly IReadOnlyList<CapabilityDescriptor> _descriptors;

    protected CapabilityProviderBase(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
    }

    public abstract ProviderId Id { get; }

    public IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;

    /// <summary>The descriptor for <paramref name="capability"/>, or null when this provider does not serve it.</summary>
    public CapabilityDescriptor? GetDescriptor(CapabilityId capability) =>
        _descriptors.FirstOrDefault(descriptor => descriptor.Id == capability);

    public abstract ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation);
}