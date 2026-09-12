namespace Spatial.Host.Api;

/// <summary>Maps the typed public host API (ADR-0033): geometry, transforms, stores, rendering (ADR-0044), tiles (ADR-0046) and the neutral admin surface (ADR-0041).</summary>
public static class SpatialApi
{
    public static void MapSpatialApi(this IEndpointRouteBuilder app, AdminOptions admin, IngestOptions ingest)
    {
        GeometryEndpoints.Map(app);
        TransformEndpoints.Map(app);
        StoreEndpoints.Map(app);
        RenderEndpoints.Map(app);
        TileEndpoints.Map(app);
        AdminEndpoints.Map(app, admin, ingest);
    }
}
