namespace Spatial.Cli;

/// <summary>
/// The command groups the CLI dispatches to (ADR-0052). Each group owns its
/// own catalog specs and handler, so a new group is one file plus one
/// switch arm in the catalog.
/// </summary>
public static class CommandGroups
{
    /// <summary>Runs the command described by <paramref name="context"/>.</summary>
    public static Task<int> DispatchAsync(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Group switch
        {
            "host" => HostCommands.RunAsync(context),
            "dataset" => DatasetCommands.RunAsync(context),
            "map" => MapCommands.RunAsync(context),
            "project" => ProjectCommands.RunAsync(context),
            _ => throw new CliUsageException($"Unknown command '{context.Command}'."),
        };
    }
}
