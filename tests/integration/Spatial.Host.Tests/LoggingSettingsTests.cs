using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace Spatial.Host.Tests;

/// <summary>
/// Logging configuration resolution (ADR-0045): minimum levels come from the
/// standard <c>Logging:LogLevel</c> section, Seq settings from
/// <c>Spatial:Logging:Seq:*</c> with environment fallbacks, and a blank Seq
/// URL leaves the host with console-only logging (no external services).
/// </summary>
public sealed class LoggingSettingsTests
{
    [Fact]
    public void Defaults_are_information_and_warning_with_no_seq()
    {
        var settings = Resolve();

        Assert.Equal(LogEventLevel.Information, settings.MinimumLevel);
        Assert.Equal(LogEventLevel.Warning, settings.AspNetCoreLevel);
        Assert.Null(settings.SeqUrl);
        Assert.Null(settings.SeqApiKey);
        Assert.False(settings.SeqEnabled);
    }

    [Fact]
    public void Reads_levels_from_the_logging_section()
    {
        var settings = Resolve(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error",
        });

        Assert.Equal(LogEventLevel.Debug, settings.MinimumLevel);
        Assert.Equal(LogEventLevel.Error, settings.AspNetCoreLevel);
    }

    [Theory]
    [InlineData("not-a-level")]
    [InlineData("")]
    [InlineData("42")]
    public void Unrecognised_levels_fall_back(string value)
    {
        Assert.Equal(LogEventLevel.Warning, LoggingSettings.ParseLevel(value, LogEventLevel.Warning));
    }

    [Fact]
    public void Reads_seq_from_configuration_and_marks_the_sink_enabled()
    {
        var settings = Resolve(new Dictionary<string, string?>
        {
            ["Spatial:Logging:Seq:Url"] = "http://seq:5341",
            ["Spatial:Logging:Seq:ApiKey"] = "secret-key",
        });

        Assert.Equal("http://seq:5341", settings.SeqUrl);
        Assert.Equal("secret-key", settings.SeqApiKey);
        Assert.True(settings.SeqEnabled);
    }

    [Fact]
    public void Environment_variables_supply_the_seq_settings()
    {
        var settings = Resolve(new Dictionary<string, string?>
        {
            ["SPATIAL_SEQ_URL"] = "http://localhost:5341",
            ["SPATIAL_SEQ_API_KEY"] = "env-key",
        });

        Assert.Equal("http://localhost:5341", settings.SeqUrl);
        Assert.Equal("env-key", settings.SeqApiKey);
    }

    [Fact]
    public void Configuration_wins_over_the_environment_fallback()
    {
        var settings = Resolve(new Dictionary<string, string?>
        {
            ["Spatial:Logging:Seq:Url"] = "http://configured",
            ["SPATIAL_SEQ_URL"] = "http://environment",
        });

        Assert.Equal("http://configured", settings.SeqUrl);
    }

    [Fact]
    public void Blank_seq_url_leaves_the_sink_disabled()
    {
        var settings = Resolve(new Dictionary<string, string?>
        {
            ["Spatial:Logging:Seq:Url"] = "   ",
            ["SPATIAL_SEQ_URL"] = "",
        });

        Assert.False(settings.SeqEnabled);
    }

    [Fact]
    public void First_non_empty_skips_blanks()
    {
        Assert.Equal("second", LoggingSettings.FirstNonEmpty(null, "", "  ", "second", "third"));
        Assert.Null(LoggingSettings.FirstNonEmpty(null, "", "  "));
    }

    private static LoggingSettings Resolve(Dictionary<string, string?>? values = null) =>
        LoggingSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build());
}
