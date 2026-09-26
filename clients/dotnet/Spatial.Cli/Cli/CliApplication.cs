using Spatial.Client;

namespace Spatial.Cli;

/// <summary>
/// The CLI's entry point (ADR-0052): parse, resolve settings, find the
/// command, run it through the gateway, and map every failure to a stable
/// exit code and a structured error. Kept as a static class with a named
/// method so it is directly testable and the quality gates see a real entry
/// method. Exception-to-exit-code mapping lives in <see cref="Report"/> so
/// this type carries only the happy path.
/// </summary>
public static class CliApplication
{
    /// <summary>Runs one invocation and returns its process exit code.</summary>
    public static async Task<int> RunAsync(
        string[] args,
        ICliConsole console,
        Func<CliSettings, ISpatialGateway>? gatewayFactory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);

        var parsed = ParseOrReport(args, console);
        if (parsed is null)
        {
            return ExitCodes.Usage;
        }

        return await ContinueAsync(parsed, console, gatewayFactory);
    }

    /// <summary>Parses the command line, reporting a usage error on the console; null when it does not parse.</summary>
    private static ParsedCommandLine? ParseOrReport(string[] args, ICliConsole console)
    {
        try
        {
            return CliParser.Parse(args);
        }
        catch (CliUsageException usage)
        {
            console.ErrorWriter.WriteLine($"error: invalid.arguments: {usage.Message}");
            return null;
        }
    }

    /// <summary>Renders help when it was asked for, otherwise runs the resolved command.</summary>
    private static async Task<int> ContinueAsync(
        ParsedCommandLine parsed, ICliConsole console, Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        if (HelpRequested(parsed))
        {
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Success;
        }

        return await RunAsync(parsed, console, gatewayFactory);
    }

    private static bool HelpRequested(ParsedCommandLine parsed) => parsed.WantsHelp || parsed.Group is null;

    /// <summary>One resolved invocation: its command line, settings, output and catalogued command.</summary>
    private sealed record Invocation(ParsedCommandLine Parsed, CliSettings Settings, CliOutput Output, CliCommandInfo Command)
    {
        public string Label => $"{Parsed.Group} {Parsed.Verb}";
    }

    private static async Task<int> RunAsync(
        ParsedCommandLine parsed, ICliConsole console, Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        var settings = ResolveOrReport(parsed, console);
        if (settings is null)
        {
            return ExitCodes.Usage;
        }

        return await RunCommandAsync(parsed, settings, console, gatewayFactory);
    }

    /// <summary>Resolves the invocation's settings, reporting a usage error; null when they do not resolve.</summary>
    private static CliSettings? ResolveOrReport(ParsedCommandLine parsed, ICliConsole console)
    {
        try
        {
            return CliSettings.Resolve(parsed);
        }
        catch (CliUsageException usage)
        {
            new CliOutput(console, parsed.Has("json"), parsed.Has("quiet"), parsed.Has("verbose"))
                .Error($"{parsed.Group} {parsed.Verb}", "invalid.arguments", usage.Message);
            return null;
        }
    }

    private static async Task<int> RunCommandAsync(
        ParsedCommandLine parsed, CliSettings settings, ICliConsole console, Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        var output = new CliOutput(console, settings.Json, settings.Quiet, settings.Verbose);
        var command = FindOrReport(parsed, output, console);
        return command is null
            ? ExitCodes.Usage
            : await RunAsync(new Invocation(parsed, settings, output, command), console, gatewayFactory);
    }

    /// <summary>The catalogued command, or null after reporting an unknown command and its help.</summary>
    private static CliCommandInfo? FindOrReport(ParsedCommandLine parsed, CliOutput output, ICliConsole console)
    {
        var label = $"{parsed.Group} {parsed.Verb}";
        if (CliCommandCatalog.Find(parsed.Group!, parsed.Verb) is { } command)
        {
            return command;
        }

        output.Error(label, "invalid.arguments", $"Unknown command '{label}'.");
        CliHelp.Render(console, parsed.Group, parsed.Verb);
        return null;
    }

    private static async Task<int> RunAsync(
        Invocation invocation, ICliConsole console, Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        if (UnknownOption(invocation.Parsed, invocation.Command) is { } unknown)
        {
            invocation.Output.Error(invocation.Label, "invalid.arguments", $"Unknown option '--{unknown}'.");
            CliHelp.Render(console, invocation.Parsed.Group, invocation.Parsed.Verb);
            return ExitCodes.Usage;
        }

        return await ExecuteAsync(invocation, console, gatewayFactory);
    }

    /// <summary>The first supplied option the resolved command (or the globals) does not accept.</summary>
    private static string? UnknownOption(ParsedCommandLine parsed, CliCommandInfo command)
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "help" };
        foreach (var option in CliCommandCatalog.GlobalOptions)
        {
            known.Add(option.Name);
        }

        foreach (var option in command.Options)
        {
            known.Add(option.Name);
        }

        return parsed.Options.Keys.FirstOrDefault(name => !known.Contains(name));
    }

    private static async Task<int> ExecuteAsync(
        Invocation invocation,
        ICliConsole console,
        Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        try
        {
            var settings = invocation.Settings;
            var context = new CliContext(
                invocation.Command.Group,
                invocation.Command.Verb,
                settings,
                new CliArguments(invocation.Parsed, invocation.Command),
                invocation.Output,
                (gatewayFactory ?? DefaultGateway)(settings));
            using (context.Gateway)
            {
                return await CommandGroups.DispatchAsync(context);
            }
        }
        catch (Exception exception)
        {
            return Report(exception, invocation.Output, invocation.Label, invocation.Settings.Host);
        }
    }

    /// <summary>Writes the structured error for a failure and returns its exit code.</summary>
    private static int Report(Exception exception, CliOutput output, string label, string host)
    {
        var (code, message, exitCode) = exception switch
        {
            CliUsageException usage => ("invalid.arguments", usage.Message, ExitCodes.Usage),
            CliSourceException source => ("store.unavailable", source.Message, ExitCodes.Unavailable),
            SpatialClientException spatial => (spatial.Code, spatial.Message, ExitCodes.ForSpatialCode(spatial.Code)),
            HttpRequestException http => ("store.unavailable", $"Could not reach the host at {host}: {http.Message}", ExitCodes.Unavailable),
            OperationCanceledException => ("cancelled", "The operation was cancelled.", ExitCodes.Cancelled),
            IOException io => ("invalid.arguments", io.Message, ExitCodes.Usage),
            _ => ("unexpected", exception.Message, ExitCodes.Failure),
        };
        output.Error(label, code, message);
        return exitCode;
    }

    private static HttpSpatialGateway DefaultGateway(CliSettings settings) => new(settings);
}
