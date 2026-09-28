using Npgsql;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The pooled data source a <see cref="PostgisStore"/> runs on, plus the three
/// faces built over it: the catalogue (<see cref="PostgisCatalogue"/>), the
/// features (<see cref="PostgisFeatures"/>) and the open transaction handles
/// (<see cref="PostgisTransactions"/>). It owns the <see cref="PostgisDataStore"/>
/// lifecycle — created on first use, disposed with the store — so the store
/// itself stays the contract facade (ADR-0033).
/// </summary>
internal sealed class PostgisStorage : IAsyncDisposable
{
    private readonly Lazy<PostgisDataStore> _data;
    private readonly PostgisTransactions _transactions;
    private readonly bool _createIndexes;

    public PostgisStorage(PostgisConnectionConfiguration configuration, bool createIndexes = true)
    {
        _data = new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(configuration));
        _transactions = new PostgisTransactions(this);
        _createIndexes = createIndexes;
    }

    /// <summary>Whether a dataset created through this storage gets its indexes (ADR-0081).</summary>
    public bool CreateIndexes => _createIndexes;

    public PostgisCatalogue Catalogue => new(this);

    public PostgisFeatures Features => new(this, Catalogue);

    public PostgisTransactions Transactions => _transactions;

    /// <summary>Opens a pooled connection; the caller owns the transaction and the lifecycle.</summary>
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _data.Value.OpenConnectionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transactions.DisposeAsync();
        if (_data.IsValueCreated)
        {
            await _data.Value.DisposeAsync();
        }
    }
}
