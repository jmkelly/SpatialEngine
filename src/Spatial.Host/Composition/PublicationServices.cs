using Spatial.Adapter.GeoServices;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.ArcGisRest;
using Spatial.Provider.Publications;

namespace Spatial.Host;

/// <summary>
/// Registers the publication registry (ADR-0041 §2) and the remote ArcGIS
/// REST consuming stores (ADR-0035). The registry's declared entries are the
/// neutral <c>Spatial:Publications:Declared</c> list plus every legacy
/// <c>Spatial:GeoServices:Services</c> entry projected to a Feature
/// publication, so the config shape is preserved while every publication has
/// stable layer ids. Split from the composition root so its fan-out stays
/// deliberate (ADR-0040).
/// </summary>
internal static class PublicationServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(BuildOptions(builder.Configuration));
        builder.Services.AddSingleton<IPublicationRegistry>(services => new PublicationRegistry(
            services.GetRequiredService<PublicationsOptions>(),
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
    private static PublicationsOptions BuildOptions(Microsoft.Extensions.Configuration.ConfigurationManager configuration)
    {
        var options = configuration.GetSection("Spatial:Publications").Get<PublicationsOptions>()
            ?? new PublicationsOptions();
        var declared = options.Declared.ToList();
        var geoServices = configuration.GetSection("Spatial:GeoServices").Get<GeoServicesOptions>() ?? new GeoServicesOptions();
        foreach (var service in geoServices.Services)
        {
            if (declared.Any(entry => string.Equals(entry.Name, service.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            declared.Add(new DeclaredPublicationOptions
            {
                Name = service.Name,
                Store = service.Store,
                Kind = nameof(PublicationKind.Feature),
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
