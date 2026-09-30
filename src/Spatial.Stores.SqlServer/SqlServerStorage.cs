using Microsoft.Data.SqlClient;
using Spatial.Stores.SqlServer.Configuration;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The pooled data source a <see cref="SqlServerStore"/> runs on, plus the
/// three faces built over it: the catalogue (<see cref="SqlServerCatalogue"/>),
/// the features (<see cref="SqlServerFeatures"/>) and the open transaction
/// handles (<see cref="SqlServerTransactions"/>). It owns the
/// <see cref="SqlServerDataStore"/> lifecycle — created on first use, disposed
/// with the store — so the store itself stays the contract facade (ADR-0033).
/// The faces are built per operation, so what outlives a call lives here: the
/// transaction handles, the database's collation (ADR-0121) and the discovered
/// dataset descriptions (ADR-0151).
/// </summary>
internal sealed class SqlServerStorage : IAsyncDisposable
{
    private readonly Lazy<SqlServerDataStore> _data;
    private readonly SqlServerTransactions _transactions;
    private readonly SemaphoreSlim _collationGate = new(1, 1);
    private readonly bool _createIndexes;

    /// <summary>
    /// The database's own collation, read once (ADR-0121, ADR-0124). A
    /// <c>varchar</c>/<c>nvarchar</c> column carries it, so a pushed-down sort
    /// key over one has to correct for it, and a property of the database is
    /// read once rather than per query. Held as the value itself so a cancelled
    /// or failed probe is retried rather than cached, and marked
    /// <c>volatile</c> so a reader on another thread sees a published value.
    /// </summary>
    private volatile string? _databaseCollation;

    public SqlServerStorage(
        SqlServerConnectionConfiguration configuration,
        bool createIndexes = true,
        TimeSpan? descriptionCacheTtl = null,
        TimeProvider? clock = null)
    {
        _data = new Lazy<SqlServerDataStore>(() => SqlServerDataStore.Open(configuration));
        _transactions = new SqlServerTransactions(this);
        _createIndexes = createIndexes;
        Descriptions = new SqlServerDescriptionCache(
            descriptionCacheTtl ?? SqlServerOptions.DefaultDescriptionCacheTtl, clock ?? TimeProvider.System);
    }

    /// <summary>Whether a dataset created through this storage gets its indexes (ADR-0092).</summary>
    public bool CreateIndexes => _createIndexes;

    /// <summary>
    /// The descriptions discovered so far, shared by every face of this store
    /// and dropped by the write paths (ADR-0151).
    /// </summary>
    public SqlServerDescriptionCache Descriptions { get; }

    public SqlServerCatalogue Catalogue => new(this);

    public SqlServerFeatures Features => new(this, Catalogue);

    public SqlServerTransactions Transactions => _transactions;

    /// <summary>Opens a pooled connection; the caller owns the transaction and the lifecycle.</summary>
    public Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _data.Value.OpenConnectionAsync(cancellationToken);

    /// <summary>
    /// The collation this database compares text under, or <c>null</c> when it
    /// has none to report — in which case the caller treats the database as a
    /// locale collation and says so in the <c>ORDER BY</c>, which is the
    /// direction that costs a planner step rather than the answer (ADR-0121
    /// §2). A probe that fails answers the same way: an unread collation is
    /// never treated as a byte order, and a failure caches nothing, so the next
    /// caller asks again.
    /// </summary>
    public async Task<string?> DatabaseCollationAsync(CancellationToken cancellationToken)
    {
        if (_databaseCollation is { } known)
        {
            return known;
        }

        await _collationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_databaseCollation is null)
            {
                _databaseCollation = await ProbeCollationAsync(cancellationToken).ConfigureAwait(false);
            }

            return _databaseCollation;
        }
        finally
        {
            _collationGate.Release();
        }
    }

    private async Task<string?> ProbeCollationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var rows = await SqlServerDataStore
                .ReadRowsAsync(connection, SqlServerQueries.DatabaseCollation(), [], cancellationToken)
                .ConfigureAwait(false);
            return rows.Count == 0 ? null : (string?)rows[0][0];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _transactions.DisposeAsync();
        if (_data.IsValueCreated)
        {
            _data.Value.Dispose();
        }

        _collationGate.Dispose();
    }
}
