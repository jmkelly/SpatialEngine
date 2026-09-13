using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>Runs a CLI invocation against a fake gateway and captures its output and exit code.</summary>
internal static class CliHarness
{
    public static async Task<CliRun> RunAsync(ISpatialGateway gateway, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, new TestConsole(output, error), _ => gateway);
        return new CliRun(code, output.ToString(), error.ToString());
    }
}

/// <summary>The captured result of one CLI run.</summary>
internal sealed record CliRun(int ExitCode, string Output, string Error);

/// <summary>An in-memory console for tests.</summary>
internal sealed class TestConsole(TextWriter output, TextWriter error) : ICliConsole
{
    public TextWriter Out { get; } = output;

    public TextWriter ErrorWriter { get; } = error;
}
