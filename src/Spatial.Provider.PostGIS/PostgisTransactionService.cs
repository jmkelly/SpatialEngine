using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The transaction lifecycle surface (ADR-0028): begins one provider-side
/// transaction and registers it under a runtime-minted handle, then commits
/// or rolls it back and releases the connection it owns. Failures propagate
/// to the guarded facade.
/// </summary>
internal sealed class PostgisTransactionService
{
    private readonly Lazy<PostgisDataStore> _store;
    private readonly PostgisTransactionRegistry _transactions;

    public PostgisTransactionService(
        Lazy<PostgisDataStore> store,
        PostgisTransactionRegistry transactions)
    {
        _store = store;
        _transactions = transactions;
    }

    /// <summary>Begins one provider-side transaction and registers it under a runtime minted handle.</summary>
    public async ValueTask<CapabilityResult> ExecuteBeginAsync(CapabilityInvocation invocation, ICapabilityFacilities facilities)
    {
        var connection = await _store.Value.OpenConnectionAsync(invocation.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(invocation.CancellationToken);
        var handle = facilities.Resources.Create(ProviderResourceKinds.Transaction);
        if (!_transactions.TryAdd(handle.Id, connection, transaction))
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            return CapabilityResult.Failure(PostgisDiagnostics.InvalidArgument(invocation.Capability, "a transaction for this handle already exists."));
        }

        return CapabilityResult.Success(handle);
    }

    /// <summary>Ends one transaction: commit or rollback, then release the connection it owns.</summary>
    public async ValueTask<CapabilityResult> ExecuteEndTransactionAsync(CapabilityInvocation invocation, ResourceId handle, bool commit)
    {
        if (!_transactions.TryTake(handle, out var state))
        {
            return CapabilityResult.Failure(PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the transaction is no longer active; begin a new one."));
        }

        return await FinishTransactionAsync(state, commit, invocation.CancellationToken);
    }

    private static async Task<CapabilityResult> FinishTransactionAsync(
        PostgisTransactionRegistry.TransactionState state,
        bool commit,
        CancellationToken token)
    {
        if (commit)
        {
            await state.Transaction.CommitAsync(token);
        }
        else
        {
            await state.Transaction.RollbackAsync(token);
        }

        await state.Connection.DisposeAsync();
        return CapabilityResult.Success(true);
    }
}
