using Npgsql;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The pooled data source a <see cref="PostgisStore"/> runs on, plus the three
/// faces built over it: the catalogue (<see cref="PostgisCatalogue"/>), the
/// features (<see cref="PostgisFeatures"/>) and the open transaction handles
/// (<see cref="PostgisTransactions"/>). It owns the <see cref="PostgisDataStore"/>
/// lifecycle — created on first use, disposed with the store — so the store
/// itself stays the contract facade (ADR-0033). The faces are built per
/// operation, so what outlives a call lives here: the transaction handles, the
/// database's collation (ADR-0121) and the discovered dataset descriptions
/// (ADR-0122).
/// </summary>
internal sealed class PostgisStorage : IAsyncDisposable
{
    private readonly Lazy<PostgisDataStore> _data;
    private readonly PostgisTransactions _transactions;
    private readonly SemaphoreSlim _collationGate = new(1, 1);
    private readonly bool _createIndexes;

    /// <summary>
    /// The database's own collation, read once (ADR-0121). A <c>text</c> column
    /// carries it, so a pushed-down sort key <em>and</em> a pushed-down
    /// comparison over one have to correct for it (ADR-0123); a property of the
    /// database is read once rather than per query. Held as the value itself so
    /// a cancelled or failed probe is retried rather than cached, and marked
    /// <c>volatile</c> so a reader on another thread sees a published value.
    /// </summary>
    private volatile string? _databaseCollation;

    public PostgisStorage(
        PostgisConnectionConfiguration configuration,
        bool createIndexes = true,
        TimeSpan? descriptionCacheTtl = null,
        TimeProvider? clock = null)
    {
        _data = new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(configuration));
        _transactions = new PostgisTransactions(this);
        _createIndexes = createIndexes;
        Descriptions = new PostgisDescriptionCache(
            descriptionCacheTtl ?? PostgisOptions.DefaultDescriptionCacheTtl, clock ?? TimeProvider.System);
    }

    /// <summary>Whether a dataset created through this storage gets its indexes (ADR-0092).</summary>
    public bool CreateIndexes => _createIndexes;

    /// <summary>
    /// The descriptions discovered so far, shared by every face of this store
    /// and dropped by the write paths (ADR-0122).
    /// </summary>
    public PostgisDescriptionCache Descriptions { get; }

    public PostgisCatalogue Catalogue => new(this);

    public PostgisFeatures Features => new(this, Catalogue);

    public PostgisTransactions Transactions => _transactions;

    /// <summary>Opens a pooled connection; the caller owns the transaction and the lifecycle.</summary>
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _data.Value.OpenConnectionAsync(cancellationToken);

    /// <summary>
    /// The collation this database compares text under, or <c>null</c> when the
    /// catalog has none to report — in which case the caller treats the database
    /// as a locale collation and says so in the <c>ORDER BY</c>, which is the
    /// direction that costs a planner step rather than the answer. One query
    /// per store, then cached; a cancelled probe caches nothing and the next
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
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var rows = await PostgisDataStore
                    .ReadRowsAsync(connection, PostgisQueries.DatabaseCollation(), [], cancellationToken)
                    .ConfigureAwait(false);
                _databaseCollation = rows.Count > 0 ? (string?)rows[0][0] ?? string.Empty : string.Empty;
            }

            return _databaseCollation;
        }
        finally
        {
            _collationGate.Release();
        }
    }

    /// <summary>
    /// Whether this database already compares text by bytes, which is what
    /// decides whether a pushed-down statement that compares a text column
    /// carries an explicit <c>COLLATE "C"</c> — a sort key (ADR-0121) or a
    /// predicate (ADR-0123). The answer is the cached catalog read above, so
    /// both faces ask the same question of the same property and neither keeps
    /// its own copy of it.
    /// </summary>
    public async Task<bool> ByteOrderTextAsync(CancellationToken cancellationToken) =>
        PostgisTextCollation.IsByteOrder(await DatabaseCollationAsync(cancellationToken).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        await _transactions.DisposeAsync();
        if (_data.IsValueCreated)
        {
            await _data.Value.DisposeAsync();
        }

        _collationGate.Dispose();
    }
}
