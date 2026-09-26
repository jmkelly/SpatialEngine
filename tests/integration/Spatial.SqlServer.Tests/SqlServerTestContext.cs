using Microsoft.Data.SqlClient;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The context of the containerised SQL Server tests (ADR-0073): a
/// <see cref="SqlServerStore"/> over the container's connection string plus
/// SQL helpers for seeding and assertions. Tests skip when Docker is
/// unavailable (see the fixture).
/// </summary>
internal sealed class SqlServerTestContext : IAsyncDisposable
{
    private SqlServerTestContext(SqlServerStore store, string connectionString)
    {
        Store = store;
        Attachments = new SqlServerAttachmentStore(store);
        Editor = new SqlServerEditStore(store);
        Ingest = new SqlServerIngestStore(store);
        ConnectionString = connectionString;
    }

    public SqlServerStore Store { get; }

    public SqlServerAttachmentStore Attachments { get; }

    public SqlServerEditStore Editor { get; }

    public SqlServerIngestStore Ingest { get; }

    public string ConnectionString { get; }

    public static SqlServerTestContext Create(string connectionString) =>
        new(
            new SqlServerStore(new SqlServerOptions { ConnectionString = connectionString }),
            connectionString);

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync() => await Store.DisposeAsync();
}
