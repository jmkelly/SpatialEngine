using Spatial.Contracts;

namespace Spatial.Host.Api;

/// <summary>
/// The admin gate every mutation-shaped route shares (ADR-0041 §6): the
/// configured <c>Spatial:Admin:Token</c> (env <c>SPATIAL_ADMIN_TOKEN</c>), or
/// an authenticated session carrying the admin role, presented as a bearer
/// token or a <c>?token=</c> parameter. The comparison is constant time and
/// the secret is never echoed.
/// <para>
/// One implementation, because a second copy of an authorization check is a
/// second thing to keep correct: the map routes and the staged-upload routes
/// (ADR-0089) are reachable by the same callers and must answer the same way.
/// </para>
/// </summary>
internal static class AdminAuthorization
{
    public static async Task RequireAsync(
        HttpContext context, AdminOptions admin, IAuthService auth, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(admin);
        await AuthGuard.RequireRoleAsync(auth, PresentedToken(context), admin.Token, AuthGuard.AdminRole, token)
            .ConfigureAwait(false);
    }

    /// <summary>The bearer token the caller presented, or the <c>?token=</c> fallback.</summary>
    private static string? PresentedToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var query = context.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }
}
