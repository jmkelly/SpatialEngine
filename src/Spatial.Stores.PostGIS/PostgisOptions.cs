namespace Spatial.Stores.PostGIS;

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

    /// <summary>
    /// Whether a created dataset gets its spatial and attribute indexes
    /// (ADR-0092). On by default, because a dataset with no indexes makes every
    /// pushed-down query a sequential scan; turn it off only for a bulk load
    /// that would rather build the indexes afterwards, and accept that the
    /// dataset is unindexed until it does.
    /// </summary>
    public bool CreateIndexes { get; set; } = true;

    public static PostgisOptions FromEnvironment() =>
        new() { ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty };
}
