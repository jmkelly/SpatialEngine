using Microsoft.Data.SqlClient;

namespace Spatial.Stores.SqlServer.Configuration;

/// <summary>
/// The provider's connection configuration (ADR-0028, as the SQL Server
/// provider follows it): a host-managed connection string that reaches the
/// host configuration (<c>SPATIAL_SQLSERVER_CONNECTION</c>), never through
/// invocations or the web client. The configuration is the only place the
/// secret exists; everything else talks about it through
/// <see cref="RedactedKey"/> and <see cref="Redact"/> so diagnostics never
/// leak credentials.
/// </summary>
internal sealed class SqlServerConnectionConfiguration
{
    /// <summary>The environment variable the host reads at startup.</summary>
    public const string EnvironmentVariable = "SPATIAL_SQLSERVER_CONNECTION";

    private readonly string _connectionString;
    private readonly string _database;
    private readonly string? _password;

    private SqlServerConnectionConfiguration(string connectionString)
    {
        _connectionString = connectionString;
        var builder = new SqlConnectionStringBuilder(connectionString);
        _database = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "(unset)" : builder.InitialCatalog;
        _password = builder.Password;
    }

    /// <summary>Whether a connection string is configured at all.</summary>
    public bool IsConfigured { get; private init; }

    /// <summary>Reads the configuration from the process environment.</summary>
    public static SqlServerConnectionConfiguration FromEnvironment() =>
        FromEnvironmentValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Builds the configuration from an explicit connection string (tests, in-process embedding).</summary>
    public static SqlServerConnectionConfiguration FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new SqlServerConnectionConfiguration(connectionString) { IsConfigured = true };
    }

    internal static SqlServerConnectionConfiguration FromEnvironmentValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new SqlServerConnectionConfiguration(string.Empty) { IsConfigured = false }
            : FromConnectionString(value);

    /// <summary>
    /// The secret connection string. Only the store touches it (SqlClient);
    /// it is never logged, never put in a diagnostic and never placed on the
    /// wire.
    /// </summary>
    internal string Secret => _connectionString;

    /// <summary>
    /// The redacted form of this configuration for diagnostics: the database
    /// name only (the secret is never in the message).
    /// </summary>
    public string RedactedKey => IsConfigured
        ? $"the configured SQL Server database '{_database}'"
        : $"no connection configuration (set {EnvironmentVariable} in the host environment)";

    /// <summary>
    /// Scrubs every occurrence of the raw connection string (and of the
    /// password alone, which may appear in provider detail) from a diagnostic
    /// message. Applied to every error the store surfaces.
    /// </summary>
    public string Redact(string message)
    {
        if (!IsConfigured || string.IsNullOrEmpty(message))
        {
            return message;
        }

        var scrubbed = message.Replace(_connectionString, "[redacted]", StringComparison.Ordinal);
        return _password is { Length: > 0 }
            ? scrubbed.Replace(_password, "[redacted]", StringComparison.Ordinal)
            : scrubbed;
    }
}
