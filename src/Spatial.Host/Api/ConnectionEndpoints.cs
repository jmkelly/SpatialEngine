using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The runtime store-connection surface: an admin sets, lists, removes and
/// reloads the per-store connection strings without restarting the host.
/// Every route here requires the admin bearer — even the statuses describe
/// which databases the host is wired to. Saves are blind: the host persists
/// the value without connecting, so a bad string fails on the store's next
/// use as <c>store.unavailable</c>, and <c>POST /api/connections/reload</c>
/// re-reads the file (picking up on-disk edits) and reports the resulting
/// statuses. Secrets are never returned; statuses carry configured and
/// updated-at only.
/// </summary>
internal static class ConnectionEndpoints
{
    /// <summary>The store keys with a settable connection string; anything else is invalid.arguments.</summary>
    private static readonly string[] SettableStores = ["postgis", "sqlserver"];

    public static void Map(
        IEndpointRouteBuilder app,
        AdminOptions admin,
        AuthOptions authOptions,
        IAuthService auth)
    {
        if (!admin.Enabled && !authOptions.Enabled)
        {
            return;
        }

        app.MapGet("/api/connections", (HttpContext context, IStoreConnectionRegistry connections, CancellationToken token) =>
            List(new ConnectionRoute(context, admin, auth, connections, token)))
            .Produces<IReadOnlyList<StoreConnectionResponse>>();
        app.MapPut("/api/connections/{store}", (string store, StoreConnectionRequest request, HttpContext context, IStoreConnectionRegistry connections, CancellationToken token) =>
            Set(new ConnectionRoute(context, admin, auth, connections, token), store, request))
            .Produces<StoreConnectionResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapDelete("/api/connections/{store}", (string store, HttpContext context, IStoreConnectionRegistry connections, CancellationToken token) =>
            Remove(new ConnectionRoute(context, admin, auth, connections, token), store))
            .Produces<bool>();
        app.MapPost("/api/connections/reload", (HttpContext context, IStoreConnectionRegistry connections, CancellationToken token) =>
            Reload(new ConnectionRoute(context, admin, auth, connections, token)))
            .Produces<ReloadConnectionsResponse>();
    }

    /// <summary>The seams every connection route shares: the caller and its cancellation, the authorization policy and the registry.</summary>
    private sealed record ConnectionRoute(
        HttpContext Context,
        AdminOptions Admin,
        IAuthService Auth,
        IStoreConnectionRegistry Connections,
        CancellationToken Token);

    private static async Task<IResult> List(ConnectionRoute route)
    {
        try
        {
            await Authorize(route);
            var statuses = await route.Connections.ListAsync(route.Token);
            return Results.Ok(statuses.Select(ToResponse).ToList());
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Set(ConnectionRoute route, string store, StoreConnectionRequest request)
    {
        try
        {
            await Authorize(route);
            var name = RequireSettableStore(store);
            if (request is null || string.IsNullOrWhiteSpace(request.ConnectionString))
            {
                throw SpatialException.BadArguments($"The connection string for store '{name}' must not be empty.");
            }

            return Results.Ok(ToResponse(await route.Connections.SetAsync(name, request.ConnectionString, route.Token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Remove(ConnectionRoute route, string store)
    {
        try
        {
            await Authorize(route);
            return Results.Ok(await route.Connections.RemoveAsync(RequireSettableStore(store), route.Token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Reload(ConnectionRoute route)
    {
        try
        {
            await Authorize(route);
            var statuses = await route.Connections.ReloadAsync(route.Token);
            return Results.Ok(new ReloadConnectionsResponse(statuses.Select(ToResponse).ToList()));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static string RequireSettableStore(string? store)
    {
        var name = (store ?? string.Empty).Trim().ToLowerInvariant();
        return SettableStores.Contains(name, StringComparer.Ordinal)
            ? name
            : throw SpatialException.BadArguments(
                $"Store '{store}' has no settable connection string: expected 'postgis' or 'sqlserver'.");
    }

    private static StoreConnectionResponse ToResponse(StoreConnectionStatus status) =>
        new(status.Store, status.Configured, status.UpdatedAt);

    private static async Task Authorize(ConnectionRoute route) =>
        await AdminAuthorization.RequireAsync(route.Context, route.Admin, route.Auth, route.Token).ConfigureAwait(false);
}
