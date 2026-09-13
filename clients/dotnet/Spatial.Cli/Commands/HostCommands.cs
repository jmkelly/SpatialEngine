namespace Spatial.Cli;

/// <summary>
/// The <c>host</c> command group (ADR-0052): readiness and the stores the
/// host advertises.
/// </summary>
public static class HostCommands
{
    /// <summary>Catalog metadata for this group's commands.</summary>
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("host", "health", "Read host readiness and its configured stores", "", []),
    ];

    /// <summary>Runs the <c>host</c> verb.</summary>
    public static Task<int> RunAsync(CliContext context) => context.Verb switch
    {
        "health" => HealthAsync(context),
        _ => throw new CliUsageException($"Unknown command '{context.Command}'."),
    };

    private static async Task<int> HealthAsync(CliContext context)
    {
        var health = await context.Gateway.CheckHealthAsync();
        var stores = health.Stores.Count == 0 ? "none" : string.Join(", ", health.Stores);
        context.Output.Result(
            context.Command,
            new { host = context.Settings.Host, health.Status, health.Stores },
            $"host {context.Settings.Host}: {health.Status} (stores: {stores})");
        return ExitCodes.Success;
    }
}
