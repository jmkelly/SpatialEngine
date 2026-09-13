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

        ParsedCommandLine parsed;
        try
        {
            parsed = CliParser.Parse(args);
        }
        catch (CliUsageException usage)
        {
            console.ErrorWriter.WriteLine($"error: invalid.arguments: {usage.Message}");
            return ExitCodes.Usage;
        }

        if (parsed.WantsHelp || parsed.Group is null)
        {
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Success;
        }

        CliSettings settings;
        try
        {
            settings = CliSettings.Resolve(parsed);
        }
        catch (CliUsageException usage)
        {
            new CliOutput(console, parsed.Has("json"), parsed.Has("quiet"), parsed.Has("verbose"))
                .Error($"{parsed.Group} {parsed.Verb}", "invalid.arguments", usage.Message);
            return ExitCodes.Usage;
        }

        var output = new CliOutput(console, settings.Json, settings.Quiet, settings.Verbose);
        var label = $"{parsed.Group} {parsed.Verb}";
        if (CliCommandCatalog.Find(parsed.Group, parsed.Verb) is not { } command)
        {
            output.Error(label, "invalid.arguments", $"Unknown command '{label}'.");
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Usage;
        }

        if (UnknownOption(parsed, command) is { } unknown)
        {
            output.Error(label, "invalid.arguments", $"Unknown option '--{unknown}'.");
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Usage;
        }

        return await ExecuteAsync(command, settings, parsed, output, label, gatewayFactory);
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
        CliCommandInfo command,
        CliSettings settings,
        ParsedCommandLine parsed,
        CliOutput output,
        string label,
        Func<CliSettings, ISpatialGateway>? gatewayFactory)
    {
        try
        {
            var context = new CliContext(
                command.Group,
                command.Verb,
                settings,
                new CliArguments(parsed, command),
                output,
                (gatewayFactory ?? DefaultGateway)(settings));
            using (context.Gateway)
            {
                return await CommandGroups.DispatchAsync(context);
            }
        }
        catch (Exception exception)
        {
            return Report(exception, output, label, settings.Host);
        }
    }

    /// <summary>Writes the structured error for a failure and returns its exit code.</summary>
    private static int Report(Exception exception, CliOutput output, string label, string host)
    {
        var (code, message, exitCode) = exception switch
        {
            CliUsageException usage => ("invalid.arguments", usage.Message, ExitCodes.Usage),
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
