using Spatial.Adapter.GeoServices;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;
using Spatial.Host.Api;

namespace Spatial.Host;

/// <summary>
/// The boundary adapters the host mounts beside its own typed API (ADR-0033,
/// ADR-0035, ADR-0053): the Esri GeoServices projection and its admin
/// surface, the OGC WMS/WFS projection, and the discovery page that reports
/// the whole routing table. Split out of
/// <see cref="HostComposition"/> so the composition root reads as
/// "logging, workbench, routing, health, API, boundaries" and the boundary
/// fan-out — which names every adapter implementation — lives in one place.
/// Mounted last so the discovery page sees every route.
/// </summary>
internal static class BoundaryAdapters
{
    /// <summary>Mounts the GeoServices, Esri admin, OGC and discovery surfaces.</summary>
    public static void Mount(WebApplication app, IConfiguration configuration)
    {
        var adminOptions = AdminOptions.FromConfiguration(configuration);
        var ingestOptions = IngestOptions.FromConfiguration(configuration);
        var authOptions = app.Services.GetRequiredService<AuthOptions>();
        var auth = app.Services.GetRequiredService<IAuthService>();
        var maps = app.Services.GetRequiredService<IMapRegistry>();

        // The Esri GeoServices boundary adapter (ADR-0035): mounted at
        // Spatial:GeoServices:Root, independent of the engine's own typed API.
        var geoServicesOptions = configuration.GetSection("Spatial:GeoServices").Get<GeoServicesOptions>()
            ?? new GeoServicesOptions();
        GeoServicesEndpoints.Map(app, geoServicesOptions, maps, auth, authOptions.Enabled, adminOptions.Token);
        EsriAdminEndpoints.Map(app, new EsriAdminOptions
        {
            Root = configuration["Spatial:GeoServices:AdminRoot"] ?? "/arcgis/admin",
            Token = adminOptions.Token,
            AuthEnabled = authOptions.Enabled,
            MaxBytes = ingestOptions.MaxBytes,
            MaxFeatures = ingestOptions.MaxFeatures,
            BatchSize = ingestOptions.BatchSize,
        }, maps, auth);

        // The OGC WMS/WFS boundary adapter (ADR-0053 §3): mounted at
        // Spatial:Ogc:Root, projecting the same maps read-only.
        OgcServices.Map(app);

        // The discovery page (ADR-0018): a live HTML index of every mounted
        // route and every published map. Mapped last so it reports the whole
        // routing table.
        DiscoveryPage.Map(app);
    }
}
