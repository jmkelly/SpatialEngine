using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// Builds the <see cref="CapabilityDescriptor"/>s a worker provider proxy
/// declares, from the package's validated manifest. The manifest is the
/// approved contract identity (the worker's handshake and the activation
/// compatibility check ensure the loaded provider matches it), so the
/// supervisor-side proxy can describe the worker's surface without ever
/// touching the process — and the registry's validation runs over the same
/// rules the manifest validator enforced.
/// </summary>
public static class ManifestDescriptorBuilder
{
    /// <summary>Builds the descriptor set for every capability in the manifest.</summary>
    public static IReadOnlyList<CapabilityDescriptor> ToDescriptors(PluginManifest manifest) =>
        (manifest.Capabilities ?? []).Select(ToDescriptor).ToArray();

    /// <summary>Builds one descriptor from a manifest capability.</summary>
    public static CapabilityDescriptor ToDescriptor(ManifestCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return new CapabilityDescriptor(
            CapabilityId.Parse(capability.Id),
            capability.Purpose,
            new SchemaDescriptor(capability.InputSchema),
            new SchemaDescriptor(capability.OutputSchema),
            (capability.Errors ?? []).Select(error => new ErrorVariant(error.Code, error.Description)).ToArray(),
            (capability.Permissions ?? []).Select(Permission.Parse).ToArray(),
            ManifestTraitMap.FromNames(capability.Traits ?? []),
            (capability.Examples ?? []).Select(example => new ConformanceExample(example.Name, example.Description)).ToArray());
    }
}
