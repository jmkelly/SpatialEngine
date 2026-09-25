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
        var options = builder.Configuration.GetSection("Spatial:Ogc").Get<OgcOptions>() ?? new OgcOptions();
        // OGC uses the same scheme and cache contracts as the host tile
        // service. The OGC option is adapter configuration, so it is seeded
        // from the one host default rather than introducing a second scheme.
        options.DefaultTileScheme = builder.Configuration["Spatial:Tiles:DefaultScheme"] ?? options.DefaultTileScheme;
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<OgcVectorTileService>();
    }

    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<OgcOptions>();
        var registry = app.Services.GetRequiredService<IMapRegistry>();
        OgcEndpoints.Map(app, options, registry);
    }
}
