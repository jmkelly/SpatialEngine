namespace Spatial.Host.Api;

/// <summary>
/// Where staged uploads live and how long they are kept (ADR-0083). The path
/// defaults to a directory under the system temp path, because a staged
/// upload is transient state: it is a pending document, not a dataset, and
/// nothing serves it.
/// <para>
/// The <em>byte</em> cap is not repeated here — a staged upload is bounded by
/// the same <c>Spatial:Ingest:MaxBytes</c> as a single-request upload, because
/// it is the same document arriving in pieces.
/// </para>
/// </summary>
public sealed class UploadOptions
{
    /// <summary>The directory staged upload bytes are written to.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>How long an un-ingested staged upload is kept (default 24 hours).</summary>
    public int MaxAgeHours { get; set; } = 24;

    /// <summary>
    /// Resolves the staging directory: the configured path, or a per-process
    /// directory under the system temp path. An empty configured path is a
    /// choice, not an error, so a host that never stages anything configures
    /// nothing.
    /// </summary>
    public string ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(Path))
        {
            return Path;
        }

        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"spatial-uploads-{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>Binds <c>Spatial:Uploads</c>.</summary>
    public static UploadOptions FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration.GetSection("Spatial:Uploads").Get<UploadOptions>() ?? new UploadOptions();
}
