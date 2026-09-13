namespace Spatial.Cli;

/// <summary>
/// Renders the CLI's help from the command catalog (ADR-0052): a grouped
/// index for <c>--help</c>, and a per-command page with usage, summary,
/// arguments and every option (command options first, then globals).
/// </summary>
public static class CliHelp
{
    /// <summary>Prints help for a command when it is known, otherwise the command index.</summary>
    public static void Render(ICliConsole console, string? group, string? verb)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (CliCommandCatalog.Find(group, verb) is { } command)
        {
            RenderCommand(console, command);
            return;
        }

        if (group is not null && verb is not null)
        {
            console.ErrorWriter.WriteLine($"Unknown command '{group} {verb}'.");
        }

        RenderIndex(console);
    }

    private static void RenderIndex(ICliConsole console)
    {
        console.Out.WriteLine("spatial — a declarative client for the Spatial Engine (ADR-0052)");
        console.Out.WriteLine();
        console.Out.WriteLine("Usage: spatial [global options] <group> <verb> [options]");
        console.Out.WriteLine();
        console.Out.WriteLine("Commands:");

        foreach (var command in CliCommandCatalog.Commands)
        {
            console.Out.WriteLine($"  {command.Group,-9} {command.Verb,-13} {command.Summary}");
        }

        console.Out.WriteLine();
        console.Out.WriteLine("Global options:");
        RenderOptions(console, CliCommandCatalog.GlobalOptions);
        console.Out.WriteLine();
        console.Out.WriteLine("Run 'spatial <group> <verb> --help' for a command's options.");
    }

    private static void RenderCommand(ICliConsole console, CliCommandInfo command)
    {
        console.Out.WriteLine($"spatial {command.Group} {command.Verb} — {command.Summary}");
        console.Out.WriteLine();
        console.Out.WriteLine($"Usage: spatial {command.Group} {command.Verb} {command.Arguments}".TrimEnd());
        console.Out.WriteLine();
        console.Out.WriteLine("Options:");
        RenderOptions(console, command.Options);
        console.Out.WriteLine();
        console.Out.WriteLine("Global options:");
        RenderOptions(console, CliCommandCatalog.GlobalOptions);
    }

    private static void RenderOptions(ICliConsole console, IReadOnlyList<CliOptionInfo> options)
    {
        foreach (var option in options)
        {
            var label = option.IsFlag ? $"--{option.Name}" : $"--{option.Name} <{option.ValueName}>";
            var required = option.Required ? " (required)" : string.Empty;
            console.Out.WriteLine($"  {label,-34} {option.Description}{required}");
        }
    }
}
