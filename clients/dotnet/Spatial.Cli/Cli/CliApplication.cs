using Spatial.Client;

namespace Spatial.Cli;

/// <summary>
/// The CLI's entry point (ADR-0052): parse, resolve settings, find the
/// command, run it through the gateway, and map every failure to a stable
/// exit code and a structured error. Kept as a static class with a named
/// method so it is directly testable and the quality gates see a real entry
/// method.
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

        var parsed = CliParser.Parse(args);
        if (parsed.WantsHelp || parsed.Group is null)
        {
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Success;
        }

        var settings = CliSettings.Resolve(parsed);
        var output = new CliOutput(console, settings.Json, settings.Quiet, settings.Verbose);
        var label = $"{parsed.Group} {parsed.Verb}";

        if (CliCommandCatalog.Find(parsed.Group, parsed.Verb) is not { } command)
        {
            output.Error(label, "invalid.arguments", $"Unknown command '{label}'.");
            CliHelp.Render(console, parsed.Group, parsed.Verb);
            return ExitCodes.Usage;
        }

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
        catch (CliUsageException exception)
        {
            output.Error(label, "invalid.arguments", exception.Message);
            return ExitCodes.Usage;
        }
        catch (SpatialClientException exception)
        {
            output.Error(label, exception.Code, exception.Message);
            return ExitCodes.ForSpatialCode(exception.Code);
        }
        catch (HttpRequestException exception)
        {
            output.Error(label, "store.unavailable", $"Could not reach the host at {settings.Host}: {exception.Message}");
            return ExitCodes.Unavailable;
        }
        catch (OperationCanceledException)
        {
            output.Error(label, "cancelled", "The operation was cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (IOException exception)
        {
            output.Error(label, "invalid.arguments", exception.Message);
            return ExitCodes.Usage;
        }
    }

    private static HttpSpatialGateway DefaultGateway(CliSettings settings) => new(settings);
}
