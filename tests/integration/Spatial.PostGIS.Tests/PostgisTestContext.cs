using Npgsql;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The context of the containerised PostGIS tests (ADR-0033): a
/// <see cref="PostgisStore"/> over the container's connection string plus
/// SQL helpers for seeding and assertions. Tests skip when Docker is
/// unavailable (see the fixture).
/// </summary>
internal sealed class PostgisTestContext : IAsyncDisposable
{
    private PostgisTestContext(PostgisStore store, string connectionString)
    {
        Store = store;
        Attachments = new PostgisAttachmentStore(store);
        Editor = new PostgisEditStore(store);
        Ingest = new PostgisIngestStore(store);
        ConnectionString = connectionString;
    }

    public PostgisStore Store { get; }

    public PostgisAttachmentStore Attachments { get; }

    public PostgisEditStore Editor { get; }

    public PostgisIngestStore Ingest { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// A context over the container, optionally with the description cache's
    /// window and a clock the test moves (ADR-0122) — expiry without sleeping.
    /// </summary>
    public static PostgisTestContext Create(
        string connectionString, TimeSpan? descriptionCacheTtl = null, TimeProvider? clock = null) =>
        new(
            new PostgisStore(
                new PostgisOptions
                {
                    ConnectionString = connectionString,
                    DescriptionCacheTtl = descriptionCacheTtl ?? PostgisOptions.DefaultDescriptionCacheTtl,
                },
                clock ?? TimeProvider.System),
            connectionString);

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync() => await Store.DisposeAsync();
}
