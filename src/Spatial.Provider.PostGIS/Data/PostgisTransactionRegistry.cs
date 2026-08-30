using System.Collections.Concurrent;
using Npgsql;
using Spatial.PluginSdk.Resources;

namespace Spatial.Provider.PostGIS.Data;

/// <summary>
/// The provider-side transaction registry (ADR-0022/0028): maps the
/// runtime-owned handle id (minted host-side for a worker) to the live
/// connection and transaction the worker holds. <c>begin</c> adds a state,
/// <c>commit</c>/<c>rollback</c> take it (once), and a write with a
/// transaction handle peeks it. A handle whose state is gone (committed,
/// rolled back, or the worker restarted) is an inactive transaction — the
/// runners surface that as an invalid argument naming the dead handle.
/// </summary>
internal sealed class PostgisTransactionRegistry
{
    private readonly ConcurrentDictionary<ResourceId, TransactionState> _transactions = new();

    /// <summary>Registers a freshly begun transaction under its handle id.</summary>
    public bool TryAdd(ResourceId id, NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        _transactions.TryAdd(id, new TransactionState(connection, transaction));

    /// <summary>Peeks the active transaction for one handle (enlisted writes).</summary>
    public bool TryGet(ResourceId id, out TransactionState state) => _transactions.TryGetValue(id, out state!);

    /// <summary>Removes and returns the transaction for one handle (commit/rollback end it).</summary>
    public bool TryTake(ResourceId id, out TransactionState state) => _transactions.TryRemove(id, out state!);

    /// <summary>One live provider-side transaction: the connection it owns and the open transaction.</summary>
    internal sealed record TransactionState(NpgsqlConnection Connection, NpgsqlTransaction Transaction);
}
