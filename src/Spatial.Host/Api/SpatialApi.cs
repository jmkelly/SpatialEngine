using Spatial.Contracts;

namespace Spatial.Host.Api;

/// <summary>Maps the typed public host API (ADR-0033): geometry, transforms, stores, rendering (ADR-0044), tiles (ADR-0046), map rendering (ADR-0053), the neutral admin surface (ADR-0041) and the runtime store-connection surface.</summary>
public static class SpatialApi
{
    public static void MapSpatialApi(
        this IEndpointRouteBuilder app,
        IHostEnvironment environment,
        AdminOptions admin,
        IngestOptions ingest,
        AuthOptions authOptions,
        IAuthService auth)
    {
        GeometryEndpoints.Map(app);
        TransformEndpoints.Map(app);
        StoreEndpoints.Map(app, admin, authOptions, auth);
        RenderEndpoints.Map(app);
        MapRenderEndpoints.Map(app);
        MapTileEndpoints.Map(app);
        TileEndpoints.Map(app);
        AdminEndpoints.Map(app, admin, ingest, authOptions, auth);
        ConnectionEndpoints.Map(app, admin, authOptions, auth);
        SeedEndpoints.Map(app, environment, admin, ingest, authOptions, auth);
    }
}
