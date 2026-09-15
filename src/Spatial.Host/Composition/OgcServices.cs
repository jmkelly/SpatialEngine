using Spatial.Adapter.Ogc;
using Spatial.Contracts.Providers;

namespace Spatial.Host;

/// <summary>
/// Registers and mounts the OGC WMS/WFS boundary adapter (ADR-0053 §3). The
/// options come from <c>Spatial:Ogc</c>; the routes are mounted after the
/// engine API so the OGC group is independent of the host's own routes.
/// </summary>
internal static class OgcServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(
            builder.Configuration.GetSection("Spatial:Ogc").Get<OgcOptions>() ?? new OgcOptions());
    }

    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<OgcOptions>();
        var registry = app.Services.GetRequiredService<IMapRegistry>();
        OgcEndpoints.Map(app, options, registry);
    }
}
