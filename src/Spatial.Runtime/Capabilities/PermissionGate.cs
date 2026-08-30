using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The default permission policy (plan §6.1 "permission evaluation"): a
/// required permission is granted only when it is a member of the
/// invocation's granted set. Hosts may supply another
/// <see cref="IPermissionEvaluator"/> to the runtime.
/// </summary>
internal sealed class GrantedPermissionsEvaluator : IPermissionEvaluator
{
    public IReadOnlyList<Permission> Missing(
        IReadOnlySet<Permission> granted,
        IReadOnlyList<Permission> required) =>
        required.Where(permission => !granted.Contains(permission)).ToArray();
}

internal static class PermissionDefaults
{
    /// <summary>The default set-membership permission policy the runtime uses when no evaluator is injected.</summary>
    public static IPermissionEvaluator Membership { get; } = new GrantedPermissionsEvaluator();
}

/// <summary>
/// The shared permission pre-check of the routing layer: compares the
/// caller's granted set with the resolved descriptor's required permissions
/// and returns a structured <see cref="CapabilityErrorKind.PermissionDenied"/>
/// error when something is missing — null when the caller may proceed. Both
/// the inline and the job paths run it before a provider is touched.
/// </summary>
internal static class PermissionGate
{
    public static CapabilityError? Denied(
        IPermissionEvaluator permissions,
        CapabilityInvocation invocation,
        ResolvedProvider resolved)
    {
        var missing = permissions.Missing(invocation.GrantedPermissions, resolved.Descriptor.RequiredPermissions);
        return missing.Count > 0 ? CapabilityError.PermissionDenied(missing) : null;
    }
}
