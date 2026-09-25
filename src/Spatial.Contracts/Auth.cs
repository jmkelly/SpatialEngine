using System.Security.Cryptography;
using System.Text;

namespace Spatial.Contracts;

/// <summary>
/// The identity resolved for a bearer token (ADR-0071). The issuer and
/// subject keep the phase-2 multi-issuer seam explicit; the local issuer
/// uses <c>local</c> and the configured username as the subject.
/// </summary>
public sealed record AuthIdentity(
    string Issuer,
    string Subject,
    string Username,
    IReadOnlyList<string> Roles);

/// <summary>An opaque bearer token and its expiry. The token is never persisted or logged.</summary>
public sealed record AuthToken(string Token, DateTimeOffset ExpiresAt, AuthIdentity Identity);

/// <summary>
/// Authentication operations. Implementations must validate credentials and
/// token state without exposing password or token material in failures.
/// </summary>
public interface IAuthService
{
    Task<AuthToken> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<AuthIdentity> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);

    Task LogoutAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Rotates a valid token, revoking the presented token.</summary>
    Task<AuthToken> RefreshAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Shared role enforcement for every host boundary (ADR-0071).</summary>
public static class AuthGuard
{
    public const string AdminRole = "admin";
    public const string WriterRole = "writer";

    /// <summary>
    /// Resolves a mutation caller. The legacy static token is accepted only as
    /// a migration path; new callers must present a valid local opaque bearer
    /// with <paramref name="requiredRole"/>. Query parameters are deliberately
    /// not accepted here: callers decide whether their legacy route permits
    /// the compatibility form.
    /// </summary>
    public static async Task<AuthIdentity> RequireRoleAsync(
        IAuthService auth,
        string? presentedToken,
        string? legacyToken,
        string requiredRole,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            throw new SpatialException(
                SpatialException.AuthUnauthorized,
                "An authentication bearer is required.");
        }

        if (!string.IsNullOrWhiteSpace(legacyToken)
            && FixedTimeEquals(presentedToken, legacyToken))
        {
            return new AuthIdentity("legacy", "admin", "admin", [AdminRole]);
        }

        AuthIdentity identity;
        try
        {
            identity = await auth.ValidateTokenAsync(presentedToken, cancellationToken);
        }
        catch (SpatialException exception) when (
            exception.Code == SpatialException.AuthFailed
            && !string.IsNullOrWhiteSpace(legacyToken))
        {
            // Keep the old static-token migration surface's 403 contract while
            // new deployments report invalid opaque bearers as auth.failed/401.
            throw new SpatialException(SpatialException.AuthForbidden, "The admin bearer is not valid.");
        }
        if (!identity.Roles.Contains(requiredRole, StringComparer.OrdinalIgnoreCase)
            && !(requiredRole == AdminRole && identity.Roles.Contains(WriterRole, StringComparer.OrdinalIgnoreCase)))
        {
            throw new SpatialException(
                SpatialException.AuthForbidden,
                $"The authenticated identity does not have the '{requiredRole}' role.");
        }

        return identity;
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
