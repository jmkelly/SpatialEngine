using System.Globalization;
using Serilog;
using Serilog.Events;

namespace Spatial.Host;

/// <summary>
/// Host logging (ADR-0045): Serilog over the framework logging abstractions.
/// The console sink is always on; the Seq sink is added only when a server URL
/// is configured, so the host remains independently executable with no
/// external services (ADR-0018). Minimum levels come from the existing
/// <c>Logging:LogLevel</c> section so <c>appsettings.json</c> keeps working.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>Configuration key for the Seq server URL.</summary>
    public const string SeqUrlKey = "Spatial:Logging:Seq:Url";

    /// <summary>Configuration key for the optional Seq API key.</summary>
    public const string SeqApiKeyKey = "Spatial:Logging:Seq:ApiKey";

    /// <summary>Environment fallback for the Seq server URL (the Aspire-injected channel).</summary>
    public const string SeqUrlEnvironmentVariable = "SPATIAL_SEQ_URL";

    /// <summary>Environment fallback for the Seq API key.</summary>
    public const string SeqApiKeyEnvironmentVariable = "SPATIAL_SEQ_API_KEY";

    /// <summary>Replaces the default logging providers with Serilog (ADR-0045).</summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        var settings = LoggingSettings.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(settings);

        builder.Host.UseSerilog((_, _, configuration) =>
        {
            configuration
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service.name", "Spatial.Host")
                .MinimumLevel.Is(settings.MinimumLevel)
                .MinimumLevel.Override("Microsoft.AspNetCore", settings.AspNetCoreLevel)
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);

            if (settings.SeqUrl is not null)
            {
                configuration.WriteTo.Seq(
                    settings.SeqUrl,
                    apiKey: settings.SeqApiKey,
                    formatProvider: CultureInfo.InvariantCulture);
            }
        });
    }
}

/// <summary>
/// The resolved logging configuration (ADR-0045): levels from the standard
/// <c>Logging:LogLevel</c> section, Seq settings from
/// <c>Spatial:Logging:Seq:*</c> with environment fallbacks. A pure value so
/// the resolution rules are unit-testable without a host.
/// </summary>
internal sealed record LoggingSettings(
    LogEventLevel MinimumLevel,
    LogEventLevel AspNetCoreLevel,
    string? SeqUrl,
    string? SeqApiKey)
{
    /// <summary>Whether the Seq sink will be wired (a server URL is present).</summary>
    public bool SeqEnabled => SeqUrl is not null;

    public static LoggingSettings FromConfiguration(IConfiguration configuration) => new(
        ParseLevel(configuration["Logging:LogLevel:Default"], LogEventLevel.Information),
        ParseLevel(configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning),
        FirstNonEmpty(
            configuration[LoggingSetup.SeqUrlKey],
            configuration[LoggingSetup.SeqUrlEnvironmentVariable]),
        FirstNonEmpty(
            configuration[LoggingSetup.SeqApiKeyKey],
            configuration[LoggingSetup.SeqApiKeyEnvironmentVariable]));

    /// <summary>Parses a Serilog level, falling back when absent or unrecognised.</summary>
    internal static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : fallback;

    /// <summary>The first non-blank candidate, or <c>null</c> when none is set.</summary>
    internal static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
