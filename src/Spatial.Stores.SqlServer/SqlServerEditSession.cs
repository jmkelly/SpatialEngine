using Microsoft.Data.SqlClient;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// One feature-editing session (ADR-0037): the connection of a store-owned
/// transaction handle, or a fresh autocommit connection that the session
/// owns. Created by <see cref="SqlServerStore.OpenEditSessionAsync"/> so the
/// editor and the transaction store share exactly one connection lifecycle.
/// </summary>
internal sealed class SqlServerEditSession(SqlConnection connection, SqlTransaction? transaction, bool ownsConnection) : IAsyncDisposable
{
    public SqlConnection Connection { get; } = connection;

    public SqlTransaction? Transaction { get; } = transaction;

    /// <summary>Builds a command on this session's connection and transaction, with the values bound positionally.</summary>
    public SqlCommand CreateCommand(string sql, object?[] values)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
        }

        return command;
    }

    public ValueTask DisposeAsync() => ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
