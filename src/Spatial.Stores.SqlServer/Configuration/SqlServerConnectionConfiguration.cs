using Microsoft.Data.SqlClient;

namespace Spatial.Stores.SqlServer.Configuration;

/// <summary>
/// The provider's connection configuration (ADR-0028, as the SQL Server
/// provider follows it): a host-managed connection string that reaches the
/// host configuration (<c>SPATIAL_SQLSERVER_CONNECTION</c>) or the admin-gated
/// runtime registry, never through a data route. The configuration is the only place the
/// secret exists; everything else talks about it through
/// <see cref="RedactedKey"/> and <see cref="Redact"/> so diagnostics never
/// leak credentials.
/// </summary>
internal sealed class SqlServerConnectionConfiguration
{
    /// <summary>The environment variable the host reads at startup.</summary>
    public const string EnvironmentVariable = "SPATIAL_SQLSERVER_CONNECTION";

    private readonly string _snapshot;
    private readonly Func<string?>? _resolve;

    private SqlServerConnectionConfiguration(string snapshot, Func<string?>? resolve)
    {
        _snapshot = snapshot;
        _resolve = resolve;
    }

    /// <summary>Whether a connection string is currently in effect.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    /// <summary>Reads the configuration from the process environment.</summary>
    public static SqlServerConnectionConfiguration FromEnvironment() =>
        FromEnvironmentValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Builds the configuration from an explicit connection string (tests, in-process embedding).</summary>
    public static SqlServerConnectionConfiguration FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Validate(connectionString);
        return new SqlServerConnectionConfiguration(connectionString, null);
    }

    /// <summary>
    /// Builds the configuration over a resolver the caller owns (the runtime
    /// connection registry): every read reflects the resolver's current
    /// answer, so a connection set after construction takes effect without a
    /// rebuild. A null, missing or whitespace answer reads as unconfigured.
    /// </summary>
    public static SqlServerConnectionConfiguration FromResolver(Func<string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return new SqlServerConnectionConfiguration(string.Empty, resolve);
    }

    internal static SqlServerConnectionConfiguration FromEnvironmentValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new SqlServerConnectionConfiguration(string.Empty, null)
            : FromConnectionString(value);

    /// <summary>
    /// The secret connection string. Only the store touches it (SqlClient);
    /// it is never logged, never put in a diagnostic and never placed on the
    /// wire.
    /// </summary>
    internal string Secret => _resolve?.Invoke() ?? _snapshot;

    /// <summary>
    /// The redacted form of this configuration for diagnostics: the database
    /// name only (the secret is never in the message).
    /// </summary>
    public string RedactedKey => IsConfigured
        ? $"the configured SQL Server database '{Database()}'"
        : $"no connection configuration (set {EnvironmentVariable} in the host environment)";

    /// <summary>
    /// Scrubs every occurrence of the raw connection string (and of the
    /// password alone, which may appear in provider detail) from a diagnostic
    /// message. Applied to every error the store surfaces.
    /// </summary>
    public string Redact(string message)
    {
        var secret = Secret;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrEmpty(message))
        {
            return message;
        }

        var scrubbed = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return Password() is { Length: > 0 } password
            ? scrubbed.Replace(password, "[redacted]", StringComparison.Ordinal)
            : scrubbed;
    }

    private static void Validate(string connectionString)
    {
        try
        {
            _ = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"The connection string is not valid: {exception.Message}", exception);
        }
    }

    private string Database()
    {
        try
        {
            var database = new SqlConnectionStringBuilder(Secret).InitialCatalog;
            return string.IsNullOrWhiteSpace(database) ? "(unset)" : database;
        }
        catch (ArgumentException)
        {
            // A resolver-backed string is stored blind and may not parse;
            // diagnostics must still answer without throwing.
            return "(unset)";
        }
    }

    private string? Password()
    {
        try
        {
            return new SqlConnectionStringBuilder(Secret).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
