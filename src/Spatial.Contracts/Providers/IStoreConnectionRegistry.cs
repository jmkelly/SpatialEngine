namespace Spatial.Contracts.Providers;

/// <summary>
/// The runtime status of one store connection: whether a connection string
/// is in effect for the store key and when it was last set. It never carries
/// the secret itself — statuses cross HTTP and logs, connection strings do
/// not.
/// </summary>
public sealed record StoreConnectionStatus(string Store, bool Configured, DateTimeOffset? UpdatedAt);

/// <summary>
/// The runtime store-connection registry: one settable connection string per
/// store key (for example <c>postgis</c>), layered over the host's startup
/// configuration. A runtime entry wins over the startup value while it
/// exists; removing it falls back to startup. Implementations persist entries
/// so they survive restarts, and resolve reads synchronously from memory so
/// the stores can pick up a change on their next operation without a
/// restart or a rebuild.
/// </summary>
/// <remarks>
/// Store names are normalised to lowercase and must be identifier-shaped
/// (<c>[a-z][a-z0-9_]*</c>); anything else is <c>invalid.arguments</c>.
/// <see cref="TryGetConnectionString"/> is the in-process read path for the
/// stores only — its out value is a secret and must never be serialised,
/// logged or placed on the wire. Every other member answers metadata.
/// </remarks>
public interface IStoreConnectionRegistry
{
    /// <summary>
    /// Reads the runtime entry for <paramref name="store"/>, or reports that
    /// none exists (in which case the caller falls back to startup
    /// configuration). Synchronous and allocation-light: the stores call it
    /// per operation.
    /// </summary>
    bool TryGetConnectionString(string store, out string? connectionString);

    /// <summary>
    /// Persists <paramref name="connectionString"/> for <paramref name="store"/>
    /// and returns the resulting status. The value is stored blind: it is not
    /// connected during the save, so a bad string fails on the store's next
    /// use as <c>store.unavailable</c>, not here.
    /// </summary>
    Task<StoreConnectionStatus> SetAsync(
        string store, string connectionString, CancellationToken cancellationToken = default);

    /// <summary>Returns the status of every stored connection, never the secrets.</summary>
    Task<IReadOnlyList<StoreConnectionStatus>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the runtime entry for <paramref name="store"/> and reports
    /// whether one existed. The store falls back to startup configuration.
    /// </summary>
    Task<bool> RemoveAsync(string store, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads the persisted entries (picking up on-disk edits) and returns
    /// the resulting statuses. A corrupt file is <c>store.unavailable</c>.
    /// </summary>
    Task<IReadOnlyList<StoreConnectionStatus>> ReloadAsync(CancellationToken cancellationToken = default);
}
