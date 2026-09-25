using Microsoft.Extensions.Configuration;
using Spatial.Contracts;

namespace Spatial.Host.Api;

/// <summary>One local user configured for phase-1 authentication.</summary>
public sealed class AuthUserOptions
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
}

/// <summary>Local-user authentication configuration (ADR-0071).</summary>
public sealed class AuthOptions
{
    public const string UsernameEnvironmentVariable = "SPATIAL_ADMIN_USERNAME";
    public const string PasswordHashEnvironmentVariable = "SPATIAL_ADMIN_PASSWORD_HASH";

    public List<AuthUserOptions> Users { get; set; } = [];
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Whether login can mint tokens for at least one configured user.</summary>
    public bool Enabled => Users.Any(user =>
        !string.IsNullOrWhiteSpace(user.Username) && !string.IsNullOrWhiteSpace(user.PasswordHash));

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Spatial:Auth");
        var options = new AuthOptions
        {
            TokenLifetime = section.GetValue<TimeSpan?>("TokenLifetime") ?? TimeSpan.FromHours(12),
        };
        options.Users = [.. section.GetSection("Users").GetChildren()
            .Select(child => child.Get<AuthUserOptions>())
            .Where(user => user is not null)
            .Select(user => user!)];
        var username = Environment.GetEnvironmentVariable(UsernameEnvironmentVariable);
        var hash = Environment.GetEnvironmentVariable(PasswordHashEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(hash))
        {
            options.Users =
            [
                new AuthUserOptions { Username = username, PasswordHash = hash, Roles = [AuthGuard.AdminRole] },
            ];
        }

        if (options.TokenLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Spatial:Auth:TokenLifetime must be positive.");
        }

        return options;
    }
}
