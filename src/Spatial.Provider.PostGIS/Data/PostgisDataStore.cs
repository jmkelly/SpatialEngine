using Npgsql;
using Spatial.Provider.PostGIS.Configuration;

namespace Spatial.Provider.PostGIS.Data;

/// <summary>
/// The Npgsql leaves of the provider (ADR-0028): an <see cref="NpgsqlDataSource"/>
/// created lazily from the host-managed connection configuration. Every method
/// here is deliberately tiny (bounded complexity) — the real behaviour it
/// feeds is validated and covered above it (SQL in
/// <see cref="PostgisQueries"/>, row mapping in the pure mappers), and the
/// store itself is exercised by the containerised integration suite. All
/// exceptions bubble to the runners, which map and redact them.
/// </summary>
internal sealed class PostgisDataStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private PostgisDataStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>Creates a store over the configured connection string (validates it locally, no connection yet).</summary>
    public static PostgisDataStore Open(PostgisConnectionConfiguration configuration) =>
        new(NpgsqlDataSource.Create(configuration.Secret));

    /// <summary>Opens a pooled connection, honouring cancellation (database-command cancellation, ADR-0028).</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await _dataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>Runs one query and materialises every row as boxed value arrays.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadRowsAsync(
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader, reader.FieldCount));
        }

        return rows;
    }

    /// <summary>Runs one non-query statement (insert/commit-side work) and returns its row count.</summary>
    public static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Opens a command over the connection with the given parameters bound positionally.</summary>
    public static async Task<NpgsqlDataReader> ExecuteReaderAsync(
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteReaderAsync(cancellationToken);
    }

    internal static NpgsqlCommand BuildCommand(NpgsqlConnection connection, string sql, IReadOnlyList<object?> parameters)
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
    public static object?[] ReadRow(NpgsqlDataReader reader, int count)
    {
        var raw = new object[count];
        reader.GetValues(raw);
        return raw;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
