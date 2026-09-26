using Spatial.Contracts;
using Spatial.Contracts.Http;

namespace Spatial.Client;

/// <summary>
/// The auth surface of the .NET client (ADR-0071): authenticate a configured
/// local user, revoke or rotate the bearer, and read the authenticated
/// identity. Split from <see cref="SpatialClient"/> so the client's fan-out
/// stays deliberate (ADR-0040); reach it through
/// <see cref="SpatialClient.Auth"/>.
/// </summary>
public sealed class SpatialAuthClient
{
    private readonly SpatialClientTransport _transport;
    private readonly SpatialClientSession _session;

    internal SpatialAuthClient(SpatialClientTransport transport, SpatialClientSession session)
    {
        _transport = transport;
        _session = session;
    }

    /// <summary>Authenticates a configured local user and returns an opaque bearer.</summary>
    public async Task<AuthToken> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<AuthTokenResponse>(
            "/api/auth/login", new LoginRequest(username, password), cancellationToken);
        _session.Token = response.Token;
        return new AuthToken(response.Token, response.ExpiresAt,
            new AuthIdentity("local", username, username, []));
    }

    /// <summary>Revokes an opaque bearer.</summary>
    public async Task LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        await _transport.SendNoContentAsync(HttpMethod.Post, "/api/auth/logout", token, cancellationToken);
        if (string.Equals(_session.Token, token, StringComparison.Ordinal)) _session.Token = null;
    }

    /// <summary>Rotates an opaque bearer.</summary>
    public async Task<AuthToken> RefreshAsync(string token, CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync<AuthTokenResponse>(
            HttpMethod.Post, "/api/auth/refresh", null, token, cancellationToken);
        _session.Token = response.Token;
        return new AuthToken(response.Token, response.ExpiresAt,
            new AuthIdentity("local", string.Empty, string.Empty, []));
    }

    /// <summary>Reads the authenticated identity and roles.</summary>
    public async Task<AuthIdentity> MeAsync(string token, CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync<AuthIdentityResponse>(
            HttpMethod.Get, "/api/auth/me", null, token, cancellationToken);
        return new AuthIdentity(response.Issuer, response.Subject, response.Username, response.Roles);
    }
}
