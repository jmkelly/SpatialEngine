namespace Spatial.Cli;

/// <summary>
/// The <c>project</c> command group (ADR-0052): read, plan and apply the
/// declarative spatial project file, and export the host's current datasets
/// and publications back to one.
/// </summary>
public static class ProjectCommands
{
    /// <summary>Catalog metadata for this group's commands.</summary>
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("project", "init", "Write a starter project file", "",
        [
            new("force", "", "Overwrite an existing project file", IsFlag: true),
        ]),
        new("project", "plan", "Show what apply would change, without mutating", "",
        [
            new("force", "", "Also plan reloading datasets that already exist", IsFlag: true),
        ]),
        new("project", "apply", "Ingest datasets and publish maps from the project file", "",
        [
            new("force", "", "Reload datasets that already exist", IsFlag: true),
        ]),
        new("project", "export", "Serialise the host's datasets and publications to the project file", "",
        [
            new("force", "", "Overwrite an existing project file", IsFlag: true),
        ]),
    ];

    /// <summary>Runs a <c>project</c> verb.</summary>
    public static Task<int> RunAsync(CliContext context) => context.Verb switch
    {
        "init" => InitAsync(context),
        "plan" => PlanAsync(context),
        "apply" => ApplyAsync(context),
        "export" => ExportAsync(context),
        _ => throw new CliUsageException($"Unknown command '{context.Command}'."),
    };

    private static Task<int> InitAsync(CliContext context) =>
        throw new CliUsageException("'project init' is not implemented yet.");

    private static Task<int> PlanAsync(CliContext context) =>
        throw new CliUsageException("'project plan' is not implemented yet.");

    private static Task<int> ApplyAsync(CliContext context) =>
        throw new CliUsageException("'project apply' is not implemented yet.");

    private static Task<int> ExportAsync(CliContext context) =>
        throw new CliUsageException("'project export' is not implemented yet.");
}
