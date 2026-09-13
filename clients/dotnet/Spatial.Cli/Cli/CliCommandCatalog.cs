namespace Spatial.Cli;

/// <summary>
/// The CLI's command catalog (ADR-0052): every command's help and option
/// metadata, assembled from each command group so a new group contributes
/// its own <c>Specs</c> without editing a shared list.
/// </summary>
public static class CliCommandCatalog
{
    /// <summary>Options accepted by every command.</summary>
    public static IReadOnlyList<CliOptionInfo> GlobalOptions { get; } =
    [
        new("host", "URL", "Host base address (env SPATIAL_HOST; default http://127.0.0.1:5201)"),
        new("token", "TOKEN", "Admin token for mutations (env SPATIAL_ADMIN_TOKEN; never echoed)"),
        new("store", "NAME", "Default store: demo, memory or postgis (writes default memory)"),
        new("project", "PATH", "Project file path (default spatial.json)"),
        new("geoservices-root", "PATH", "GeoServices route prefix (default /arcgis/rest/services)"),
        new("json", "", "Emit a machine-readable {ok, command, data} envelope", IsFlag: true),
        new("quiet", "", "Suppress human output on success", IsFlag: true),
        new("verbose", "", "Print request details to stderr", IsFlag: true),
        new("dry-run", "", "Validate and print the plan without mutating the host", IsFlag: true),
        new("timeout", "SECONDS", "Per-request timeout in seconds (default 100)"),
        new("help", "", "Show this help", IsFlag: true),
    ];

    /// <summary>Every command, in help order.</summary>
    public static IReadOnlyList<CliCommandInfo> Commands { get; } =
    [
        .. HostCommands.Specs,
        .. DatasetCommands.Specs,
        .. MapCommands.Specs,
        .. ProjectCommands.Specs,
    ];

    /// <summary>Finds a command by group and verb, or <see langword="null"/> when unknown.</summary>
    public static CliCommandInfo? Find(string? group, string? verb) =>
        group is null || verb is null
            ? null
            : Commands.FirstOrDefault(command =>
                string.Equals(command.Group, group, StringComparison.Ordinal)
                && string.Equals(command.Verb, verb, StringComparison.Ordinal));
}
