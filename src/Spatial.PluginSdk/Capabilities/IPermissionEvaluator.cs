namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// Decides whether an invocation's granted permission set satisfies a
/// capability's requirements. The runtime uses this to enforce
/// <c>RequiredPermissions</c>; hosts may plug in a policy (for example one
/// that consults a scoped authorization service) without changing routing.
/// The default implementation checks set membership.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>Required permissions the granted set does not satisfy, or an empty list.</summary>
    IReadOnlyList<Permission> Missing(
        IReadOnlySet<Permission> granted,
        IReadOnlyList<Permission> required);
}
