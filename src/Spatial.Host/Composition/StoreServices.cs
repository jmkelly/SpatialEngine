using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.Demo;
using Spatial.Provider.Memory;
using Spatial.Provider.PostGIS;

namespace Spatial.Host;

/// <summary>
/// Registers the keyed data stores (ADR-0033): the read-only demo store, the
/// ephemeral writable in-memory provider (ADR-0042) and the PostGIS store,
/// each exposed through the granular capability interfaces it actually
/// implements. Split from the composition root so its fan-out stays
/// deliberate (ADR-0040).
/// </summary>
internal static class StoreServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        // The one typed seam over the keyed stores (ADR-0033): adapters and
        // the host API resolve store capabilities through it, never through
        // the container.
        builder.Services.AddSingleton<IStoreRegistry, KeyedStoreRegistry>();
        ConfigureDemo(builder);
        ConfigureMemory(builder);
        ConfigurePostgis(builder);
    }

    private static void ConfigureDemo(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<DemoStore>();
        builder.Services.AddKeyedSingleton<IDataCatalogue, DemoStore>("demo");
        builder.Services.AddKeyedSingleton<IFeatureStore, DemoStore>("demo");
        builder.Services.AddSingleton<IDemoWork>(services => services.GetRequiredService<DemoStore>());
    }

    private static void ConfigureMemory(WebApplicationBuilder builder)
    {
        // The ephemeral writable in-memory provider (ADR-0042): always available
        // under the key `memory`, so ingest/publish work with no database.
        builder.Services.AddSingleton<MemoryStore>();
        builder.Services.AddSingleton<MemoryEditor>();
        builder.Services.AddSingleton<MemoryAttachments>();
        builder.Services.AddSingleton<MemoryIngest>();
        builder.Services.AddKeyedSingleton<IDataCatalogue>("memory", (services, _) => services.GetRequiredService<MemoryStore>());
        builder.Services.AddKeyedSingleton<IFeatureStore>("memory", (services, _) => services.GetRequiredService<MemoryStore>());
        builder.Services.AddKeyedSingleton<IFeatureLookup>("memory", (services, _) => services.GetRequiredService<MemoryStore>());
        builder.Services.AddKeyedSingleton<IFeatureEditStore>("memory", (services, _) => services.GetRequiredService<MemoryEditor>());
        builder.Services.AddKeyedSingleton<IFeatureAttachmentStore>("memory", (services, _) => services.GetRequiredService<MemoryAttachments>());
        builder.Services.AddKeyedSingleton<ITransactionStore>("memory", (services, _) => services.GetRequiredService<MemoryStore>());
        builder.Services.AddKeyedSingleton<IDatasetIngest>("memory", (services, _) => services.GetRequiredService<MemoryIngest>());
    }

    private static void ConfigurePostgis(WebApplicationBuilder builder)
    {
        var postgisOptions = builder.Configuration.GetSection("Spatial:Postgis").Get<PostgisOptions>()
            ?? PostgisOptions.FromEnvironment();
        if (string.IsNullOrWhiteSpace(postgisOptions.ConnectionString))
        {
            postgisOptions = PostgisOptions.FromEnvironment();
        }

        builder.Services.AddSingleton(postgisOptions);
        builder.Services.AddSingleton<PostgisStore>();
        builder.Services.AddSingleton<PostgisAttachmentStore>();
        builder.Services.AddSingleton<PostgisEditStore>();
        builder.Services.AddSingleton<PostgisIngestStore>();
        builder.Services.AddKeyedSingleton<IDataCatalogue>("postgis", (services, _) => services.GetRequiredService<PostgisStore>());
        builder.Services.AddKeyedSingleton<IFeatureStore>("postgis", (services, _) => services.GetRequiredService<PostgisStore>());
        builder.Services.AddKeyedSingleton<IFeatureAttachmentStore>("postgis", (services, _) => services.GetRequiredService<PostgisAttachmentStore>());
        builder.Services.AddKeyedSingleton<IFeatureEditStore>("postgis", (services, _) => services.GetRequiredService<PostgisEditStore>());
        builder.Services.AddKeyedSingleton<IFeatureLookup>("postgis", (services, _) => services.GetRequiredService<PostgisStore>());
        builder.Services.AddKeyedSingleton<ITransactionStore>("postgis", (services, _) => services.GetRequiredService<PostgisStore>());
        builder.Services.AddKeyedSingleton<IDatasetIngest>("postgis", (services, _) => services.GetRequiredService<PostgisIngestStore>());
    }
}
