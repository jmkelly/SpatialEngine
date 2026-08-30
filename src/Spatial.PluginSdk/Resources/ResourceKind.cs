using System.Diagnostics.CodeAnalysis;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The dotted lowercase kind of a resource (plan §8 "opaque handles for
/// datasets, transactions and intermediate results"): for example
/// <c>dataset</c>, <c>feature.scan</c> or <c>transaction</c>. Kinds use the
/// same dotted identifier rule as capability names and permissions; they are
/// diagnostic and structural, never a security boundary.
/// </summary>
public readonly record struct ResourceKind
{
    public ResourceKind(string name)
    {
        var problem = QualifiedNameRules.DescribeProblem(name);
        if (problem is not null)
        {
            throw new ArgumentException($"Not a resource kind: {problem}", nameof(name));
        }

        Name = name;
    }

    /// <summary>Dotted lowercase resource kind, for example <c>dataset</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Parses a resource kind, throwing <see cref="FormatException"/> when the
    /// text is not a dotted lowercase name.
    /// </summary>
    public static ResourceKind Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var kind))
        {
            throw new FormatException(
                $"'{text}' is not a valid resource kind: expected a dotted lowercase name such as 'dataset'.");
        }

        return kind;
    }

    /// <summary>
    /// Attempts to parse a resource kind. Returns <c>false</c> without
    /// throwing for null or malformed input.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out ResourceKind kind)
    {
        kind = default;
        if (text is null || QualifiedNameRules.DescribeProblem(text) is not null)
        {
            return false;
        }

        kind = new ResourceKind(text);
        return true;
    }

    public override string ToString() => Name;
}
