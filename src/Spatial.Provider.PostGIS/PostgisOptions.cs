namespace Spatial.Provider.PostGIS;

/// <summary>
/// Options for the PostGIS store (ADR-0033): the connection string comes
/// from host configuration (<c>Spatial:Postgis:ConnectionString</c>) or the
/// <c>SPATIAL_POSTGIS_CONNECTION</c> environment variable — never from a
/// request body. Empty means unconfigured: every operation throws
/// <c>store.unavailable</c> naming the setting.
/// </summary>
public sealed class PostgisOptions
{
    public const string EnvironmentVariable = "SPATIAL_POSTGIS_CONNECTION";

    public string ConnectionString { get; set; } = string.Empty;

    public static PostgisOptions FromEnvironment() =>
        new() { ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty };
}
