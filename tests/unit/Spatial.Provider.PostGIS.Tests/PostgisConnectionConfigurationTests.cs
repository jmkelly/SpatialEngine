using Spatial.Provider.PostGIS.Configuration;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The host-managed connection configuration (ADR-0028): environment
/// reading, redacted keys and the guarantee that the raw secret is never
/// part of a diagnostic-facing property.
/// </summary>
public sealed class PostgisConnectionConfigurationTests
{
    [Fact]
    public void Empty_environment_means_unconfigured()
    {
        var configuration = PostgisConnectionConfiguration.FromEnvironmentValue(null);

        Assert.False(configuration.IsConfigured);
        Assert.Contains(PostgisConnectionConfiguration.EnvironmentVariable, configuration.RedactedKey);
    }

    [Fact]
    public void A_connection_string_configures_the_provider()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=localhost;Port=5432;Database=geodata;Username=spatial;Password=pw");

        Assert.True(configuration.IsConfigured);
        Assert.Contains("'geodata'", configuration.RedactedKey);
        Assert.DoesNotContain("pw", configuration.RedactedKey);
        Assert.Equal("Host=localhost;Port=5432;Database=geodata;Username=spatial;Password=pw", configuration.Secret);
    }

    [Fact]
    public void From_environment_reads_the_process_environment()
    {
        var name = PostgisConnectionConfiguration.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "Host=localhost;Database=spatial;Username=admin;Password=pw");

            var configuration = PostgisConnectionConfiguration.FromEnvironment();

            Assert.True(configuration.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Whitespace_environment_is_unconfigured()
    {
        Assert.False(PostgisConnectionConfiguration.FromEnvironmentValue("   ").IsConfigured);
    }
}
