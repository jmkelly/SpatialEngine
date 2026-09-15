using Spatial.Adapter.GeoServices;
using Spatial.Maps;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Stores.ArcGisRest;

namespace Spatial.Host;

/// <summary>
/// Registers the map registry (ADR-0053 §2) and the remote ArcGIS REST
/// consuming stores (ADR-0035). The registry's declared entries are the
/// neutral <c>Spatial:Maps:Declared</c> list plus every legacy
/// <c>Spatial:GeoServices:Services</c> entry projected to a Feature-only map,
/// so the config shape is preserved while every map has stable layer ids. A
/// pre-ADR-0053 legacy map path (<c>Spatial:Publications:Path</c>, kept as a deprecated alias)
/// is read once and migrated.
/// Split from the composition root so its fan-out stays deliberate (ADR-0040).
/// </summary>
internal static class MapServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(BuildOptions(builder.Configuration));
        builder.Services.AddSingleton<IMapRegistry>(services => new MapRegistry(
            services.GetRequiredService<MapsOptions>(),
            (store, cancellationToken) =>
            {
                var catalogue = services.GetKeyedService<IDataCatalogue>(store);
                return catalogue is null
                    ? Task.FromResult<IReadOnlyList<DatasetSummary>>([])
                    : catalogue.ListAsync(null, cancellationToken);
            }));
        ConfigureRemote(builder);
    }

    /// <summary>Projects the legacy GeoServices service map plus the neutral declared list into one registry seed.</summary>
    private static MapsOptions BuildOptions(Microsoft.Extensions.Configuration.ConfigurationManager configuration)
    {
        var options = configuration.GetSection("Spatial:Maps").Get<MapsOptions>()
            ?? new MapsOptions();

        // A pre-ADR-0053 legacy map file migrates on first read when the new
        // path does not exist; the legacy file is never rewritten.
        if (string.IsNullOrWhiteSpace(options.LegacyPath))
        {
            var legacyPath = configuration["Spatial:Publications:Path"];
            if (!string.IsNullOrWhiteSpace(legacyPath))
            {
                options.LegacyPath = legacyPath;
            }
        }

        var declared = options.Declared.ToList();
        var geoServices = configuration.GetSection("Spatial:GeoServices").Get<GeoServicesOptions>() ?? new GeoServicesOptions();
        foreach (var service in geoServices.Services)
        {
            if (declared.Any(entry => string.Equals(entry.Name, service.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            declared.Add(new DeclaredMapOptions
            {
                Name = service.Name,
                Store = service.Store,
                Services = [nameof(MapServiceKind.FeatureServer)],
            });
        }

        options.Declared = declared;
        return options;
    }

    /// <summary>Registers the ArcGIS REST consuming provider's keyed stores (ADR-0035).</summary>
    private static void ConfigureRemote(WebApplicationBuilder builder)
    {
        var arcGisOptions = builder.Configuration.GetSection("Spatial:ArcGisRest").Get<ArcGisRestOptions>()
            ?? new ArcGisRestOptions();
        if (arcGisOptions.Services.Count == 0)
        {
            return;
        }

        builder.Services.AddSingleton(new HttpClient());
        foreach (var remote in arcGisOptions.Services)
        {
            builder.Services.AddKeyedSingleton<IDataCatalogue>(remote.Name, (services, _) =>
                new ArcGisRestStore(services.GetRequiredService<HttpClient>(), remote, arcGisOptions.Token));
            builder.Services.AddKeyedSingleton<IFeatureStore>(remote.Name, (services, _) =>
                new ArcGisRestStore(services.GetRequiredService<HttpClient>(), remote, arcGisOptions.Token));
        }
    }
}
