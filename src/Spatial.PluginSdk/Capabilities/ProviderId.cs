using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The stable identity of a capability provider: a dotted lowercase name plus
/// a positive version, rendered as <c>name@version</c> (for example
/// <c>nts@1</c>). The name is the stable provider id used by deterministic
/// resolution (plan §9, step 4); the version lets two provider generations
/// run side by side (plan §10.4).
/// </summary>
public readonly record struct ProviderId : IComparable<ProviderId>
{
    public ProviderId(string name, int version)
    {
        ThrowIfInvalidName(name, nameof(name));
        ThrowIfInvalidVersion(version, nameof(version));
        Name = name;
        Version = version;
    }

    /// <summary>Dotted lowercase provider name, for example <c>nts</c> or <c>postgis</c>.</summary>
    public string Name { get; }

    /// <summary>Positive integer provider version, starting at 1.</summary>
    public int Version { get; }

    /// <summary>
    /// Parses <c>name@version</c>, throwing <see cref="FormatException"/> when
    /// the text is not a valid provider id.
    /// </summary>
    public static ProviderId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var id))
        {
            throw new FormatException(
                $"'{text}' is not a valid provider id: expected 'name@version' with a dotted lowercase name and a version of 1 or greater.");
        }

        return id;
    }

    /// <summary>
    /// Attempts to parse <c>name@version</c>. Returns <c>false</c> without
    /// throwing for null, malformed or out-of-range input.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out ProviderId id)
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

        id = new ProviderId(name, version);
        return true;
    }

    /// <summary>Ordinal by name, then version — the stable provider ordering.</summary>
    public int CompareTo(ProviderId other)
    {
        var byName = string.CompareOrdinal(Name, other.Name);
        return byName != 0 ? byName : Version.CompareTo(other.Version);
    }

    public static bool operator <(ProviderId left, ProviderId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ProviderId left, ProviderId right) => left.CompareTo(right) <= 0;

    public static bool operator >(ProviderId left, ProviderId right) => left.CompareTo(right) > 0;

    public static bool operator >=(ProviderId left, ProviderId right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Name}@{Version}";

    internal static void ThrowIfInvalidName(string? name, string parameterName)
    {
        var problem = QualifiedNameRules.DescribeProblem(name);
        if (problem is not null)
        {
            throw new ArgumentException($"Not a provider name: {problem}", parameterName);
        }
    }

    internal static void ThrowIfInvalidVersion(int version, string parameterName)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, version, "A provider version must be a positive integer (1 or greater).");
        }
    }
}
