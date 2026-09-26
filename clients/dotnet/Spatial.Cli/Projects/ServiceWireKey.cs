using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;
using Spatial.Contracts.Providers;

namespace Spatial.Cli;

/// <summary>
/// The pinned JSON wire key for each <see cref="MapServiceKind"/>. The keys
/// are declared once, on the enum itself
/// (<see cref="JsonStringEnumMemberNameAttribute"/>), so the project file, the
/// map view and the host's own <c>maps.json</c> cannot drift apart; a member
/// without a pinned name falls back to its lower-cased name.
/// </summary>
internal static class ServiceWireKey
{
    private static readonly FrozenDictionary<MapServiceKind, string> Keys = Enum
        .GetValues<MapServiceKind>()
        .ToFrozenDictionary(service => service, PinnedName);

    internal static string Of(MapServiceKind service) =>
        Keys.TryGetValue(service, out var key) ? key : service.ToString().ToLowerInvariant();

    private static string PinnedName(MapServiceKind service)
    {
        var member = typeof(MapServiceKind).GetField(service.ToString(), BindingFlags.Public | BindingFlags.Static);
        return member?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? service.ToString().ToLowerInvariant();
    }
}
