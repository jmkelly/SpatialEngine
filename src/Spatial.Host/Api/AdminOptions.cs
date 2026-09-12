namespace Spatial.Host.Api;

/// <summary>
/// The admin surface's security configuration (ADR-0041 §6):
/// <c>Spatial:Admin:Token</c> (environment fallback
/// <c>SPATIAL_ADMIN_TOKEN</c>) gates every mutation route. An empty token
/// means the mutation routes are not mounted at all; the secret is never
/// echoed in a response or a log.
/// </summary>
public sealed class AdminOptions
{
    public const string EnvironmentVariable = "SPATIAL_ADMIN_TOKEN";

    /// <summary>The bearer token; empty disables the mutation routes.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Whether a token is configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Token);

    /// <summary>Binds <c>Spatial:Admin</c>, falling back to the environment secret.</summary>
    public static AdminOptions FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var options = configuration.GetSection("Spatial:Admin").Get<AdminOptions>() ?? new AdminOptions();
        if (!options.Enabled)
        {
            options.Token = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? options.Token;
        }

        return options;
    }
}

/// <summary>
/// Bounds for the neutral ingest route (ADR-0041 §6): a byte cap, a feature
/// cap, the decoder page size and the accepted format names. Over-limit is
/// <c>invalid.arguments</c>, never a truncated load.
/// </summary>
public sealed class IngestOptions
{
    /// <summary>The maximum upload size in bytes (default 100 MiB).</summary>
    public long MaxBytes { get; set; } = 104_857_600;

    /// <summary>The maximum number of decoded features (default one million).</summary>
    public int MaxFeatures { get; set; } = 1_000_000;

    /// <summary>Features per decoded page.</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>The accepted format names (<c>geojson</c>, <c>ndjson</c>, <c>csv</c>).</summary>
    public IReadOnlyList<string> Formats { get; set; } = ["geojson", "ndjson", "csv"];

    /// <summary>Binds <c>Spatial:Ingest</c>.</summary>
    public static IngestOptions FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration.GetSection("Spatial:Ingest").Get<IngestOptions>() ?? new IngestOptions();
}
