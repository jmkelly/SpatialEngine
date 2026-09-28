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
/// </summary>
internal sealed class SqlServerStorage : IAsyncDisposable
{
    private readonly Lazy<SqlServerDataStore> _data;
    private readonly SqlServerTransactions _transactions;
    private readonly bool _createIndexes;

    public SqlServerStorage(SqlServerConnectionConfiguration configuration, bool createIndexes = true)
    {
        _data = new Lazy<SqlServerDataStore>(() => SqlServerDataStore.Open(configuration));
        _transactions = new SqlServerTransactions(this);
        _createIndexes = createIndexes;
    }

    /// <summary>Whether a dataset created through this storage gets its indexes (ADR-0092).</summary>
    public bool CreateIndexes => _createIndexes;

    public SqlServerCatalogue Catalogue => new(this);

    public SqlServerFeatures Features => new(this, Catalogue);

    public SqlServerTransactions Transactions => _transactions;

    /// <summary>Opens a pooled connection; the caller owns the transaction and the lifecycle.</summary>
    public Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _data.Value.OpenConnectionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transactions.DisposeAsync();
        if (_data.IsValueCreated)
        {
            _data.Value.Dispose();
        }
    }
}
