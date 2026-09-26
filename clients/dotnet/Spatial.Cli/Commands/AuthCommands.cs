namespace Spatial.Cli;

/// <summary>Login and logout commands for the local opaque bearer flow (ADR-0071).</summary>
public static class AuthCommands
{
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("auth", "login", "Authenticate and cache an opaque bearer", "",
            [new("username", "NAME", "Configured username"), new("password", "PASSWORD", "Password (not echoed)")]),
        new("auth", "logout", "Revoke the cached bearer", "", []),
    ];

    /// <summary>The verb-to-handler table; an unlisted verb is a usage error.</summary>
    private static readonly Dictionary<string, Func<CliContext, Task<int>>> Handlers = new(StringComparer.Ordinal)
    {
        ["login"] = LoginAsync,
        ["logout"] = LogoutAsync,
    };

    public static Task<int> RunAsync(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Handlers.TryGetValue(context.Verb, out var handler)
            ? handler(context)
            : throw new CliUsageException($"Unknown auth command '{context.Verb}'.");
    }

    private static async Task<int> LoginAsync(CliContext context)
    {
        var username = context.Arguments.Require("username");
        var password = context.Arguments.Require("password");
        var token = await context.Gateway.LoginAsync(username, password);
        AuthTokenStore.Write(token.Token);
        context.Output.Result(context.Command, new { username, expiresAt = token.ExpiresAt },
            $"Authenticated {username}; bearer cached.");
        return ExitCodes.Success;
    }

    private static async Task<int> LogoutAsync(CliContext context)
    {
        var token = context.Settings.Token
            ?? throw new CliUsageException("No cached bearer; run 'spatial auth login' first.");
        await context.Gateway.LogoutAsync(token);
        AuthTokenStore.Clear();
        context.Output.Result(context.Command, new { loggedOut = true }, "Bearer revoked and removed from the cache.");
        return ExitCodes.Success;
    }
}
