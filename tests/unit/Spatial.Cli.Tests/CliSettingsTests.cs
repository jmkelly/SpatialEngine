using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>Settings resolution from flags and environment (ADR-0052).</summary>
public sealed class CliSettingsTests
{
    [Fact]
    public void Resolve_uses_environment_and_defaults()
    {
        var parsed = CliParser.Parse(["dataset", "list"]);

        var settings = CliSettings.Resolve(parsed, name => name switch
        {
            "SPATIAL_HOST" => "http://env-host:1234",
            "SPATIAL_ADMIN_TOKEN" => "env-token",
            _ => null,
        });

        Assert.Equal("http://env-host:1234", settings.Host);
        Assert.Equal("env-token", settings.Token);
        Assert.Equal(CliSettings.DefaultStore, settings.Store);
        Assert.Equal(CliSettings.DefaultProjectPath, settings.ProjectPath);
        Assert.Equal(CliSettings.DefaultGeoServicesRoot, settings.GeoServicesRoot);
        Assert.Equal(CliSettings.DefaultTimeout, settings.Timeout);
    }

    [Fact]
    public void Resolve_prefers_flags_over_environment()
    {
        var parsed = CliParser.Parse(
            ["map", "list", "--host", "http://flag:9", "--store", "postgis", "--json", "--dry-run", "--timeout", "2.5"]);

        var settings = CliSettings.Resolve(parsed, _ => "http://env");

        Assert.Equal("http://flag:9", settings.Host);
        Assert.Equal("postgis", settings.Store);
        Assert.True(settings.Json);
        Assert.True(settings.DryRun);
        Assert.Equal(TimeSpan.FromSeconds(2.5), settings.Timeout);
    }

    [Fact]
    public void Resolve_defaults_the_host_when_nothing_is_set()
    {
        var settings = CliSettings.Resolve(CliParser.Parse(["host", "health"]), _ => null);

        Assert.Equal(CliSettings.DefaultHost, settings.Host);
        Assert.Null(settings.Token);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    public void Resolve_rejects_an_invalid_timeout(string value)
    {
        var parsed = CliParser.Parse(["host", "health", "--timeout", value]);

        Assert.Throws<CliUsageException>(() => CliSettings.Resolve(parsed, _ => null));
    }
}
