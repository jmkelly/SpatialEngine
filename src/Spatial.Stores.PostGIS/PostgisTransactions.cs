using System.Collections.Concurrent;
using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
namespace Spatial.Stores.PostGIS;

/// <summary>
/// The open transaction handles of a <see cref="PostgisStore"/>: a string key
/// over the connection and the Npgsql transaction it began, so a write, an
/// edit or an attachment call can join the caller's transaction instead of
/// opening its own (ADR-0033). Handles are removed as they end and every
/// handle releases its connection when the store is disposed.
/// </summary>
internal sealed class PostgisTransactions(PostgisStorage storage) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PostgisTransactionEntry> _handles = new();

    /// <summary>Begins a transaction on a pooled connection and returns its handle.</summary>
    public async Task<string> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await storage.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var handle = Guid.NewGuid().ToString("N");
        _handles[handle] = new PostgisTransactionEntry(connection, transaction);
        return handle;
    }

    /// <summary>Appends a batch inside the transaction a handle names.</summary>
    public Task<int> WriteAsync(
        string handle,
        PostgisDatasetName name,
        DatasetDescription description,
        FeatureBatch batch,
        CancellationToken cancellationToken) =>
        Require(handle).WriteAsync(name, description, batch, cancellationToken);

    /// <summary>
    /// Commits or rolls the handle back, then always releases its connection.
    /// The datasets it wrote through are forgotten either way: a commit made
    /// their writes visible and a rollback decided they never happened, and a
    /// description read before that decision cannot describe them after it
    /// (ADR-0122). A commit whose outcome is in doubt — one that threw — is the
    /// case where holding a description would be worst.
    /// </summary>
    public async Task<bool> EndAsync(string handle, bool commit, CancellationToken cancellationToken)
    {
        if (!_handles.TryRemove(handle, out var entry))
        {
            throw SpatialException.BadArguments($"Unknown transaction '{handle}'.");
        }

        try
        {
            return await entry.CompleteAsync(commit, cancellationToken);
        }
        finally
        {
            foreach (var dataset in entry.Written)
            {
                storage.Descriptions.Invalidate(dataset);
            }
        }
    }

    /// <summary>
    /// The connection a store-side edit runs on: the handle's connection when
    /// one is named, otherwise a fresh autocommit connection the caller owns
    /// (ADR-0037).
    /// </summary>
    public async Task<PostgisEditSession> OpenEditSessionAsync(string? handle, CancellationToken cancellationToken)
    {
        if (handle is not null)
        {
            var entry = Require(handle);
            return new PostgisEditSession(entry.Connection, entry.Transaction, ownsConnection: false);
        }

        var connection = await storage.OpenConnectionAsync(cancellationToken);
        return new PostgisEditSession(connection, transaction: null, ownsConnection: true);
    }

    /// <summary>The entry a handle names, or an <c>invalid.arguments</c> failure when it is unknown.</summary>
    private PostgisTransactionEntry Require(string handle) =>
        _handles.TryGetValue(handle, out var entry)
            ? entry
            : throw SpatialException.BadArguments($"Unknown transaction '{handle}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _handles.Values)
        {
            await entry.DisposeAsync();
        }

        _handles.Clear();
    }
}

/// <summary>
/// One open transaction: the connection it began on and the Npgsql handle
/// itself, plus the datasets written through it — the ones whose descriptions
/// have to be forgotten when it ends (ADR-0122).
/// </summary>
internal sealed record PostgisTransactionEntry(NpgsqlConnection Connection, NpgsqlTransaction Transaction)
{
    private readonly ConcurrentDictionary<PostgisDatasetName, byte> _written = new();

    /// <summary>The datasets a write joined this transaction to.</summary>
    public ICollection<PostgisDatasetName> Written => _written.Keys;

    /// <summary>Appends a batch inside this transaction.</summary>
    public async Task<int> WriteAsync(
        PostgisDatasetName name, DatasetDescription description, FeatureBatch batch, CancellationToken cancellationToken)
    {
        _written.TryAdd(name, 0);
        return await PostgisWriteOperations.WriteOnAsync(Connection, Transaction, name, description, batch, cancellationToken);
    }

    /// <summary>Commits or rolls back, then always releases the connection.</summary>
    public async Task<bool> CompleteAsync(bool commit, CancellationToken cancellationToken)
    {
        try
        {
            if (commit)
            {
                await Transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await Transaction.RollbackAsync(cancellationToken);
            }

            return true;
        }
        finally
        {
            await DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
