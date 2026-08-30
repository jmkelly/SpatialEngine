using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// A stable, versioned capability identifier — the contract name clients
/// request and providers declare (ADR-0007). Rendered as
/// <c>name@version</c>, for example <c>spatial.geometry.buffer@1</c>: a
/// dotted lowercase name plus a positive integer version.
/// </summary>
public readonly record struct CapabilityId : IComparable<CapabilityId>
{
    public CapabilityId(string name, int version)
    {
        ThrowIfInvalidName(name, nameof(name));
        ThrowIfInvalidVersion(version, nameof(version));
        Name = name;
        Version = version;
    }

    /// <summary>Dotted lowercase capability name, for example <c>spatial.feature.scan</c>.</summary>
    public string Name { get; }

    /// <summary>Positive integer contract version, starting at 1 (ADR-0007).</summary>
    public int Version { get; }

    /// <summary>
    /// Parses <c>name@version</c>, throwing <see cref="FormatException"/> when
    /// the text is not a valid capability id.
    /// </summary>
    public static CapabilityId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var id))
        {
            throw new FormatException(
                $"'{text}' is not a valid capability id: expected 'name@version' with a dotted lowercase name and a version of 1 or greater.");
        }

        return id;
    }

    /// <summary>
    /// Attempts to parse <c>name@version</c>. Returns <c>false</c> without
    /// throwing for null, malformed or out-of-range input.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out CapabilityId id)
    {
        id = default;
        if (text is null)
        {
            return false;
        }

        var at = text.IndexOf('@');
        if (at <= 0 || at == text.Length - 1)
        {
            return false;
        }

        var name = text[..at];
        var versionText = text[(at + 1)..];
        if (QualifiedNameRules.DescribeProblem(name) is not null)
        {
            return false;
        }

        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            return false;
        }

        id = new CapabilityId(name, version);
        return true;
    }

    /// <summary>Ordinal by name, then version — the stable resolution order.</summary>
    public int CompareTo(CapabilityId other)
    {
        var byName = string.CompareOrdinal(Name, other.Name);
        return byName != 0 ? byName : Version.CompareTo(other.Version);
    }

    public static bool operator <(CapabilityId left, CapabilityId right) => left.CompareTo(right) < 0;

    public static bool operator <=(CapabilityId left, CapabilityId right) => left.CompareTo(right) <= 0;

    public static bool operator >(CapabilityId left, CapabilityId right) => left.CompareTo(right) > 0;

    public static bool operator >=(CapabilityId left, CapabilityId right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Name}@{Version}";

    internal static void ThrowIfInvalidName(string? name, string parameterName)
    {
        var problem = QualifiedNameRules.DescribeProblem(name);
        if (problem is not null)
        {
            throw new ArgumentException($"Not a capability name: {problem}", parameterName);
        }
    }

    internal static void ThrowIfInvalidVersion(int version, string parameterName)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, version, "A capability version must be a positive integer (1 or greater).");
        }
    }
}
