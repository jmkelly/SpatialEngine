using System.Diagnostics.CodeAnalysis;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// A permission a caller must hold to invoke a capability, for example
/// <c>spatial.feature.read</c>. Permissions use the same dotted lowercase
/// identifier rule as capability and provider names; they are granted to an
/// invocation through <see cref="CapabilityInvocation.GrantedPermissions"/>
/// and checked by the runtime before the provider is called.
/// </summary>
public readonly record struct Permission
{
    public Permission(string name)
    {
        var problem = QualifiedNameRules.DescribeProblem(name);
        if (problem is not null)
        {
            throw new ArgumentException($"Not a permission name: {problem}", nameof(name));
        }

        Name = name;
    }

    /// <summary>Dotted lowercase permission name, for example <c>spatial.feature.write</c>.</summary>
    public string Name { get; }

    public static Permission Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var permission))
        {
            throw new FormatException(
                $"'{text}' is not a valid permission: expected a dotted lowercase name such as 'spatial.feature.read'.");
        }

        return permission;
    }

    public static bool TryParse([NotNullWhen(true)] string? text, out Permission permission)
    {
        permission = default;
        if (text is null || QualifiedNameRules.DescribeProblem(text) is not null)
        {
            return false;
        }

        permission = new Permission(text);
        return true;
    }

    public override string ToString() => Name;
}