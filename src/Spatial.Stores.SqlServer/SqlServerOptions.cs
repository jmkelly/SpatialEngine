namespace Spatial.Stores.SqlServer;

/// <summary>
/// Options for the SQL Server store (ADR-0033): the connection string comes
/// from host configuration (<c>Spatial:SqlServer:ConnectionString</c>) or the
/// <c>SPATIAL_SQLSERVER_CONNECTION</c> environment variable — never from a
/// request body. Empty means unconfigured: every operation throws
/// <c>store.unavailable</c> naming the setting.
/// </summary>
public sealed class SqlServerOptions
{
    public const string EnvironmentVariable = "SPATIAL_SQLSERVER_CONNECTION";

    public string ConnectionString { get; set; } = string.Empty;

    public static SqlServerOptions FromEnvironment() =>
        new() { ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty };
}
