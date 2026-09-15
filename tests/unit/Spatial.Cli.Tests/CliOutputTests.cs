namespace Spatial.Cli.Tests;

/// <summary>
/// The CLI output seam (ADR-0052): diagnostics go to the error stream only
/// under <c>--verbose</c>, so quiet invocations stay machine-clean.
/// </summary>
public sealed class CliOutputTests
{
    [Fact]
    public void Verbose_WritesToTheErrorStream_WhenVerboseIsSet()
    {
        var error = new StringWriter();
        var output = new CliOutput(new TestConsole(new StringWriter(), error), json: false, quiet: false, verbose: true);

        output.Verbose("using store memory");

        Assert.Contains("using store memory", error.ToString());
    }

    [Fact]
    public void Verbose_StaysSilent_WhenVerboseIsNotSet()
    {
        var error = new StringWriter();
        var output = new CliOutput(new TestConsole(new StringWriter(), error), json: false, quiet: false, verbose: false);

        output.Verbose("using store memory");

        Assert.Equal(string.Empty, error.ToString());
    }
}
