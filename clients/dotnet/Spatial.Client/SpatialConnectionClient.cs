using Spatial.Contracts.Http;

namespace Spatial.Client;

/// <summary>
/// The runtime store-connection surface of the .NET client: set, list,
/// remove and reload the per-store connection strings an admin manages
/// through <c>/api/connections</c>. Every route requires the admin bearer;
/// saves are blind (a bad string fails on the store's next use), and no
/// response carries a secret. Reach it through
/// <see cref="SpatialClient.Connections"/>.
/// </summary>
public sealed class SpatialConnectionClient
{
    private readonly SpatialClientTransport _transport;
    private readonly SpatialClientSession _session;

    internal SpatialConnectionClient(SpatialClientTransport transport, SpatialClientSession session)
    {
        _transport = transport;
        _session = session;
    }

    /// <summary>Lists every runtime connection status (requires the admin token).</summary>
    public Task<IReadOnlyList<StoreConnectionResponse>> ListAsync(
        string? adminToken = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<IReadOnlyList<StoreConnectionResponse>>(
            HttpMethod.Get, "/api/connections", null, adminToken ?? _session.Token, cancellationToken);

    /// <summary>
    /// Persists the connection string for a store without connecting
    /// (requires the admin token).
    /// </summary>
    public Task<StoreConnectionResponse> SetAsync(
        string store, string connectionString, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return _transport.SendAsync<StoreConnectionResponse>(
            HttpMethod.Put,
            $"/api/connections/{Uri.EscapeDataString(store.Trim().ToLowerInvariant())}",
            SpatialClientCodec.Json(new StoreConnectionRequest(connectionString)),
            adminToken ?? _session.Token,
            cancellationToken);
    }

    /// <summary>
    /// Removes the runtime override for a store, falling back to startup
    /// configuration, and reports whether one existed (requires the admin token).
    /// </summary>
    public Task<bool> RemoveAsync(
        string store, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        return _transport.SendAsync<bool>(
            HttpMethod.Delete,
            $"/api/connections/{Uri.EscapeDataString(store.Trim().ToLowerInvariant())}",
            null,
            adminToken ?? _session.Token,
            cancellationToken);
    }

    /// <summary>
    /// Re-reads the persisted connections and reports the resulting statuses
    /// (requires the admin token).
    /// </summary>
    public Task<ReloadConnectionsResponse> ReloadAsync(
        string? adminToken = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<ReloadConnectionsResponse>(
            HttpMethod.Post, "/api/connections/reload", null, adminToken ?? _session.Token, cancellationToken);
}
