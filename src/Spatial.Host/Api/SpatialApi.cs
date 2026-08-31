namespace Spatial.Host.Api;

/// <summary>Maps the public spatial host API (plan §12) onto the application.</summary>
public static class SpatialApi
{
    public static void MapSpatialApi(this IEndpointRouteBuilder app)
    {
        CapabilityEndpoints.Map(app);
        InvocationEndpoints.Map(app);
        JobEndpoints.Map(app);
        ResourceEndpoints.Map(app);
        PluginEndpoints.Map(app);
    }
}
