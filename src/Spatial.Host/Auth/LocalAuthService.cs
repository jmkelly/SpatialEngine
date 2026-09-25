using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Spatial.Contracts;
using Spatial.Host.Api;

namespace Spatial.Host.Auth;

/// <summary>
/// In-memory phase-1 issuer: configured PBKDF2 password hashes authenticate
/// users, and only SHA-256 hashes of random opaque bearers are retained.
/// Restarting the host intentionally revokes local tokens; spatial stores
/// remain auth-free (ADR-0071).
/// </summary>
internal sealed class LocalAuthService : IAuthService
{
    private readonly AuthOptions _options;
    private readonly PasswordHasher<LocalUser> _passwordHasher = new();
    private readonly ConcurrentDictionary<string, IssuedToken> _tokens = new(StringComparer.Ordinal);

    public LocalAuthService(AuthOptions options)
    {
        _options = options;
    }

    public Task<AuthToken> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _options.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user is null || !Verify(user, password))
        {
            throw SpatialException.AuthenticationFailed();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Mint(new AuthIdentity("local", user.Username, user.Username, user.Roles)));
    }

    public Task<AuthIdentity> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Hash(token);
        if (!_tokens.TryGetValue(key, out var issued) || issued.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _tokens.TryRemove(key, out _);
            throw new SpatialException(SpatialException.AuthFailed, "The bearer token is invalid or expired.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(issued.Identity);
    }

    public Task LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tokens.TryRemove(Hash(token), out _))
        {
            throw new SpatialException(SpatialException.AuthFailed, "The bearer token is invalid or expired.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<AuthToken> RefreshAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Hash(token);
        if (!_tokens.TryGetValue(key, out var issued) || issued.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _tokens.TryRemove(key, out _);
            throw new SpatialException(SpatialException.AuthFailed, "The bearer token is invalid or expired.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _tokens.TryRemove(key, out _);
        return Task.FromResult(Mint(issued.Identity));
    }

    private bool Verify(AuthUserOptions user, string password)
    {
        try
        {
            return _passwordHasher.VerifyHashedPassword(
                new LocalUser(user.Username),
                user.PasswordHash,
                password ?? string.Empty) is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private AuthToken Mint(AuthIdentity identity)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var issued = new IssuedToken(identity, DateTimeOffset.UtcNow.Add(_options.TokenLifetime));
        _tokens[Hash(token)] = issued;
        return new AuthToken(token, issued.ExpiresAt, identity);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    private sealed record LocalUser(string Username);

    private sealed record IssuedToken(AuthIdentity Identity, DateTimeOffset ExpiresAt);
}
