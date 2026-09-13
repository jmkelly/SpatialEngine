using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>Typed argument access rejects malformed and non-finite values (ADR-0052).</summary>
public sealed class CliArgumentsTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    public void RequireDouble_rejects_a_non_finite_value(string value)
    {
        var arguments = ForSetStyle("--opacity", value);

        var exception = Assert.Throws<CliUsageException>(() => arguments.RequireDouble("opacity"));
        Assert.Contains("finite", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireDouble_accepts_a_finite_value()
    {
        var arguments = ForSetStyle("--opacity", "0.5");

        Assert.Equal(0.5, arguments.RequireDouble("opacity"));
    }

    private static CliArguments ForSetStyle(params string[] options)
    {
        var parsed = CliParser.Parse(["map", "set-style", .. options]);
        var command = CliCommandCatalog.Find("map", "set-style")!;
        return new CliArguments(parsed, command);
    }
}
