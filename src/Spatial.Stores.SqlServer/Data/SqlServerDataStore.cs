using Microsoft.Data.SqlClient;
using Spatial.Stores.SqlServer.Configuration;

namespace Spatial.Stores.SqlServer.Data;

/// <summary>
/// The SqlClient leaves of the store (ADR-0028 as the SQL Server provider
/// follows it): pooled connections opened from the host-managed connection
/// configuration. Every method here is deliberately tiny (bounded
/// complexity) — the real behaviour it feeds is validated and covered above
/// it (T-SQL in <see cref="SqlServerQueries"/>, row mapping in the pure
/// mappers), and the store itself is exercised by the containerised
/// integration suite. All exceptions bubble to the runners, which map and
/// redact them.
/// </summary>
internal sealed class SqlServerDataStore : IDisposable
{
    private readonly string _connectionString;
    private int _disposed;

    private SqlServerDataStore(SqlServerConnectionConfiguration configuration)
    {
        _connectionString = configuration.Secret;
    }

    /// <summary>Creates a store over the configured connection string (validates it locally, no connection yet).</summary>
    public static SqlServerDataStore Open(SqlServerConnectionConfiguration configuration) =>
        new(configuration);

    /// <summary>
    /// Opens a pooled connection, honouring cancellation (database-command
    /// cancellation). Connections are pooled by the connection string, so the
    /// per-operation open costs a pool hit rather than a new session.
    /// </summary>
    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs one query and materialises every row as boxed value arrays.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadRowsAsync(
        SqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadAllRowsAsync(reader, cancellationToken);
    }

    /// <summary>Runs one non-query statement (insert/commit-side work) and returns its row count.</summary>
    public static Task<int> ExecuteNonQueryAsync(
        SqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(connection, transaction: null, sql, parameters, cancellationToken);

    /// <summary>Runs one non-query statement on an explicit transaction (ingest DDL/inserts, ADR-0041).</summary>
    public static async Task<int> ExecuteNonQueryAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Opens a command over the connection with the given parameters bound positionally.</summary>
    public static async Task<SqlDataReader> ExecuteReaderAsync(
        SqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteReaderAsync(cancellationToken);
    }

    /// <summary>Reads the caller's open reader to the end, as boxed value arrays.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAllRowsAsync(
        SqlDataReader reader, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader, reader.FieldCount));
        }

        return rows;
    }

    internal static SqlCommand BuildCommand(
        SqlConnection connection, string sql, IReadOnlyList<object?> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i] ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Reads one row's values into an array (DbDataReader.GetValues is a single call).</summary>
    public static object?[] ReadRow(SqlDataReader reader, int count)
    {
        var raw = new object[count];
        reader.GetValues(raw);
        return raw;
    }

    /// <summary>
    /// Nothing to release: connections are pooled by the connection string
    /// itself, so the store only holds the (secret) configuration.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
