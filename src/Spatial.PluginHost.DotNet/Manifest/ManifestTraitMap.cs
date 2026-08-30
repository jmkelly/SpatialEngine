using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// Maps between the manifest's language-neutral trait names and the SDK's
/// <see cref="CapabilityTraits"/> flags. The names are part of the manifest
/// schema (plan §9 "streaming and cancellation behaviour"): <c>cancellable</c>,
/// <c>streaming</c>, <c>long-running</c> and <c>side-effects</c>.
/// </summary>
public static class ManifestTraitMap
{
    private static readonly (string Name, CapabilityTraits Trait)[] Entries =
    [
        ("cancellable", CapabilityTraits.Cancellable),
        ("streaming", CapabilityTraits.Streaming),
        ("long-running", CapabilityTraits.LongRunning),
        ("side-effects", CapabilityTraits.SideEffects),
    ];

    /// <summary>Every known trait name, in canonical order.</summary>
    public static IReadOnlyList<string> Names { get; } = Entries.Select(entry => entry.Name).ToArray();

    /// <summary>Whether <paramref name="name"/> is a known trait name.</summary>
    public static bool Contains(string? name) => Entries.Any(entry => entry.Name == name);

    /// <summary>Parses one trait name to its flag, or null when the name is unknown.</summary>
    public static CapabilityTraits? FromName(string? name)
    {
        foreach (var entry in Entries)
        {
            if (entry.Name == name)
            {
                return entry.Trait;
            }
        }

        return null;
    }

    /// <summary>Combines the flags for a set of names; unknown names are skipped.</summary>
    public static CapabilityTraits FromNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var traits = CapabilityTraits.None;
        foreach (var name in names)
        {
            if (FromName(name) is { } trait)
            {
                traits |= trait;
            }
        }

        return traits;
    }

    /// <summary>The canonical names for a flags value, in declaration order.</summary>
    public static IReadOnlyList<string> ToNames(CapabilityTraits traits) =>
        Entries.Where(entry => (traits & entry.Trait) != 0).Select(entry => entry.Name).ToArray();
}
