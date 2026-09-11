namespace Spatial.Host.Api;

/// <summary>Maps the typed public host API (ADR-0033).</summary>
public static class SpatialApi
{
    public static void MapSpatialApi(this IEndpointRouteBuilder app)
    {
        GeometryEndpoints.Map(app);
        TransformEndpoints.Map(app);
        StoreEndpoints.Map(app);
    }
}
