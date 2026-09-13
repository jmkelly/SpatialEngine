using System.Globalization;

namespace Spatial.Cli;

/// <summary>
/// The resolved global options for one invocation (ADR-0052): host, admin
/// token, default store, project-file path, GeoServices root, output mode and
/// timeout. Flags override environment variables; defaults are the local
/// development host and the neutral writable store.
/// </summary>
public sealed record CliSettings(
    string Host,
    string? Token,
    string Store,
    string ProjectPath,
    string GeoServicesRoot,
    bool Json,
    bool Quiet,
    bool Verbose,
    bool DryRun,
    TimeSpan Timeout)
{
    /// <summary>Default host base address.</summary>
    public const string DefaultHost = "http://127.0.0.1:5201";

    /// <summary>Default store for reads.</summary>
    public const string DefaultStore = "memory";

    /// <summary>Default project-file path.</summary>
    public const string DefaultProjectPath = "spatial.json";

    /// <summary>Default GeoServices route prefix (ADR-0035).</summary>
    public const string DefaultGeoServicesRoot = "/arcgis/rest/services";

    /// <summary>The default per-request timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Resolves settings from the parsed command line, falling back to the environment.</summary>
    public static CliSettings Resolve(ParsedCommandLine parsed, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var getEnvironment = environment ?? Environment.GetEnvironmentVariable;

        var host = NonEmpty(parsed.Last("host")) ?? NonEmpty(getEnvironment("SPATIAL_HOST")) ?? DefaultHost;
        var token = NonEmpty(parsed.Last("token")) ?? NonEmpty(getEnvironment("SPATIAL_ADMIN_TOKEN"));
        var store = NonEmpty(parsed.Last("store")) ?? DefaultStore;
        var project = NonEmpty(parsed.Last("project")) ?? DefaultProjectPath;
        var geoServicesRoot = NonEmpty(parsed.Last("geoservices-root")) ?? DefaultGeoServicesRoot;
        var seconds = NonEmpty(parsed.Last("timeout")) is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
                ? parsedSeconds
                : DefaultTimeout.TotalSeconds;

        return new CliSettings(
            host,
            token,
            store,
            project,
            geoServicesRoot,
            parsed.Has("json"),
            parsed.Has("quiet"),
            parsed.Has("verbose"),
            parsed.Has("dry-run"),
            TimeSpan.FromSeconds(seconds));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
