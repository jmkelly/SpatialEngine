using Npgsql;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// One feature-editing session (ADR-0037): the connection of a store-owned
/// transaction handle, or a fresh autocommit connection that the session
/// owns. Created by <see cref="PostgisStore.OpenEditSessionAsync"/> so the
/// editor and the transaction store share exactly one connection lifecycle.
/// </summary>
internal sealed class PostgisEditSession(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool ownsConnection) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction? Transaction { get; } = transaction;

    public ValueTask DisposeAsync() => ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
