using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Host.Api;
using Spatial.Stores.Demo;
using Spatial.Stores.Memory;
using Spatial.Stores.PostGIS;
using Spatial.Stores.SqlServer;

namespace Spatial.Host;

/// <summary>
/// Registers the keyed data stores (ADR-0033): the read-only demo store, the
/// ephemeral writable in-memory provider (ADR-0042), the PostGIS store and
/// the SQL Server store, each exposed through the granular service faces it actually
/// implements. Split from the composition root so its fan-out stays
/// deliberate (ADR-0040).
/// </summary>
internal static class StoreServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        // The one typed seam over the keyed stores (ADR-0033): adapters and
        // the host API resolve store faces through it, never through
        // the container.
        builder.Services.AddSingleton<IStoreRegistry, KeyedStoreRegistry>();
        ConfigureUploads(builder);
        ConfigureDemo(builder);
        ConfigureMemory(builder);
        ConfigurePostgis(builder);
        ConfigureSqlServer(builder);
    }

    /// <summary>
    /// The byte staging resumable uploads land in (ADR-0090). It is bounded by
    /// the ingest byte cap, because a staged upload is the same document as a
    /// single-request upload, arriving in pieces.
    /// </summary>
    private static void ConfigureUploads(WebApplicationBuilder builder)
    {
        var options = UploadOptions.FromConfiguration(builder.Configuration);
        var maxBytes = IngestOptions.FromConfiguration(builder.Configuration).MaxBytes;
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IUploadStaging>(_ => new FileUploadStaging(options, maxBytes));
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

    /// <summary>
    /// The SQL Server store (ADR-0073): the same contract faces as PostGIS,
    /// keyed <c>sqlserver</c>. Unconfigured (no connection string) it throws
    /// <c>store.unavailable</c> naming the setting.
    /// </summary>
    private static void ConfigureSqlServer(WebApplicationBuilder builder)
    {
        var sqlServerOptions = builder.Configuration.GetSection("Spatial:SqlServer").Get<SqlServerOptions>()
            ?? SqlServerOptions.FromEnvironment();
        if (string.IsNullOrWhiteSpace(sqlServerOptions.ConnectionString))
        {
            sqlServerOptions = SqlServerOptions.FromEnvironment();
        }

        builder.Services.AddSingleton(sqlServerOptions);
        builder.Services.AddSingleton<SqlServerStore>();
        builder.Services.AddSingleton<SqlServerAttachmentStore>();
        builder.Services.AddSingleton<SqlServerEditStore>();
        builder.Services.AddSingleton<SqlServerIngestStore>();
        builder.Services.AddKeyedSingleton<IDataCatalogue>("sqlserver", (services, _) => services.GetRequiredService<SqlServerStore>());
        builder.Services.AddKeyedSingleton<IFeatureStore>("sqlserver", (services, _) => services.GetRequiredService<SqlServerStore>());
        builder.Services.AddKeyedSingleton<IFeatureAttachmentStore>("sqlserver", (services, _) => services.GetRequiredService<SqlServerAttachmentStore>());
        builder.Services.AddKeyedSingleton<IFeatureEditStore>("sqlserver", (services, _) => services.GetRequiredService<SqlServerEditStore>());
        builder.Services.AddKeyedSingleton<IFeatureLookup>("sqlserver", (services, _) => services.GetRequiredService<SqlServerStore>());
        builder.Services.AddKeyedSingleton<ITransactionStore>("sqlserver", (services, _) => services.GetRequiredService<SqlServerStore>());
        builder.Services.AddKeyedSingleton<IDatasetIngest>("sqlserver", (services, _) => services.GetRequiredService<SqlServerIngestStore>());
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
