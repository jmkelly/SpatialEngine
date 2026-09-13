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

    private static Task<int> InitAsync(CliContext context)
    {
        var path = context.Settings.ProjectPath;
        RefuseOverwrite(path, context.Arguments.Has("force"));
        SpatialProjectFile.Save(path, SpatialProjectFile.Starter());
        context.Output.Result(context.Command, new { path }, path);
        return Task.FromResult(ExitCodes.Success);
    }

    private static async Task<int> PlanAsync(CliContext context)
    {
        var project = SpatialProjectFile.Load(context.Settings.ProjectPath);
        var plan = await ProjectApplier.PlanAsync(context.Gateway, project, context.Settings, context.Arguments.Has("force"));
        context.Output.Result(context.Command, plan, ProjectApplier.Describe(plan));
        return ExitCodes.Success;
    }

    private static async Task<int> ApplyAsync(CliContext context)
    {
        var project = SpatialProjectFile.Load(context.Settings.ProjectPath);
        var force = context.Arguments.Has("force");
        var plan = context.Settings.DryRun
            ? await ProjectApplier.PlanAsync(context.Gateway, project, context.Settings, force)
            : await ProjectApplier.ApplyAsync(context.Gateway, project, context.Settings, force);
        context.Output.Result(context.Command, plan, ProjectApplier.Describe(plan));
        return ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(CliContext context)
    {
        var path = context.Settings.ProjectPath;
        RefuseOverwrite(path, context.Arguments.Has("force"));
        var project = await ProjectApplier.ExportAsync(context.Gateway, context.Settings);
        SpatialProjectFile.Save(path, project);
        context.Output.Result(
            context.Command,
            new { path, datasets = project.Datasets.Count, maps = project.Maps.Count },
            $"Wrote {path} ({project.Datasets.Count} dataset(s), {project.Maps.Count} map(s)).");
        return ExitCodes.Success;
    }

    private static void RefuseOverwrite(string path, bool force)
    {
        if (File.Exists(path) && !force)
        {
            throw new CliUsageException($"Project file '{path}' already exists. Use --force to overwrite.");
        }
    }
}
