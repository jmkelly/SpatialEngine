using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Spatial.Adapter.GeoServices;
using Spatial.Host.Api;
using Spatial.Imagery.Vips;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.ArcGisRest;
using Spatial.Provider.Demo;
using Spatial.Provider.PostGIS;
using Spatial.Rendering.Skia;
using Spatial.Transformations.ProjNet;

var builder = WebApplication.CreateBuilder(args);
HostComposition.ConfigureServices(builder);

var app = builder.Build();
HostComposition.ConfigurePipeline(app, builder.Configuration);

app.Run();

/// <summary>Entry point type used by <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;

internal sealed record ReadyResponse(string Status, IReadOnlyList<string> Stores);

/// <summary>
/// The host's service and pipeline composition (ADR-0033): in-process
/// services resolved by DI, the browser workbench's static content, the typed
/// host API and the GeoServices boundary adapter. Kept out of the top-level
/// statements so each concern is a named, testable method.
/// </summary>
internal static class HostComposition
{
    /// <summary>Registers the in-process spatial services and configuration.</summary>
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // One JSON contract across the API: camelCase everywhere (ADR-0033).
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = HostApiJson.Options.PropertyNamingPolicy;
            options.SerializerOptions.DictionaryKeyPolicy = HostApiJson.Options.DictionaryKeyPolicy;
            foreach (var converter in HostApiJson.Options.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });

        builder.Services.AddOpenApi();

        // In-process spatial services (ADR-0033): interfaces resolved by DI, no
        // worker processes. The demo store is always available; the PostGIS store
        // reads its connection string from host configuration.
        var postgisOptions = builder.Configuration.GetSection("Spatial:Postgis").Get<PostgisOptions>()
            ?? PostgisOptions.FromEnvironment();
        if (string.IsNullOrWhiteSpace(postgisOptions.ConnectionString))
        {
            postgisOptions = PostgisOptions.FromEnvironment();
        }

        builder.Services.AddSingleton(postgisOptions);
        builder.Services.AddSingleton<IGeometryOperations, NtsGeometryOperations>();
        builder.Services.AddSingleton<NtsGeometryMeasures>();
        builder.Services.AddSingleton<IGeometryMeasures>(services => services.GetRequiredService<NtsGeometryMeasures>());
        builder.Services.AddSingleton<NtsGeometryProcessing>();
        builder.Services.AddSingleton<IGeometryProcessing>(services => services.GetRequiredService<NtsGeometryProcessing>());
        builder.Services.AddSingleton<NtsGeometryRelations>();
        builder.Services.AddSingleton<IGeometryRelations>(services => services.GetRequiredService<NtsGeometryRelations>());
        builder.Services.AddSingleton<ProjNetTransforms>();
        builder.Services.AddSingleton<ICrsDirectory>(services => services.GetRequiredService<ProjNetTransforms>());
        builder.Services.AddSingleton<ICoordinateTransforms>(services => services.GetRequiredService<ProjNetTransforms>());
        builder.Services.AddSingleton<DemoStore>();
        builder.Services.AddKeyedSingleton<IDataCatalogue, DemoStore>("demo");
        builder.Services.AddKeyedSingleton<IFeatureStore, DemoStore>("demo");
        builder.Services.AddSingleton<IDemoJobs>(services => services.GetRequiredService<DemoStore>());
        builder.Services.AddSingleton<PostgisStore>();
        builder.Services.AddSingleton<PostgisEditStore>();
        builder.Services.AddKeyedSingleton<IDataCatalogue, PostgisStore>("postgis");
        builder.Services.AddKeyedSingleton<IFeatureStore, PostgisStore>("postgis");
        builder.Services.AddKeyedSingleton<IFeatureEditStore>("postgis", (services, _) => services.GetRequiredService<PostgisEditStore>());
        builder.Services.AddKeyedSingleton<IFeatureLookup>("postgis", (services, _) => services.GetRequiredService<PostgisStore>());
        builder.Services.AddKeyedSingleton<ITransactionStore, PostgisStore>("postgis");

        ConfigureRemoteServices(builder);
        ConfigureRendering(builder);
    }

    /// <summary>Registers the vector renderer and the imagery pipeline (ADR-0044).</summary>
    private static void ConfigureRendering(WebApplicationBuilder builder)
    {
        var renderingOptions = builder.Configuration.GetSection("Spatial:Rendering").Get<RenderingOptions>()
            ?? new RenderingOptions();
        var imageryOptions = builder.Configuration.GetSection("Spatial:Imagery").Get<ImageryOptions>()
            ?? new ImageryOptions();
        builder.Services.AddSingleton(renderingOptions);
        builder.Services.AddSingleton(imageryOptions);
        builder.Services.AddSingleton<IRasterOperations>(new VipsRasterOperations(imageryOptions.ToSourceMap()));
        builder.Services.AddSingleton<IMapRenderer>(services => new MapRenderer(
            services.GetRequiredService<ICoordinateTransforms>(),
            services.GetRequiredService<IGeometryOperations>(),
            services.GetRequiredService<IRasterOperations>(),
            new RenderLimits(renderingOptions.MaxPixels, renderingOptions.MaxLayers)));
    }

    /// <summary>Registers the ArcGIS REST consuming provider's keyed stores (ADR-0035).</summary>
    private static void ConfigureRemoteServices(WebApplicationBuilder builder)
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

    /// <summary>Builds the request pipeline: workbench, routing, health, API and GeoServices.</summary>
    public static void ConfigurePipeline(WebApplication app, IConfiguration configuration)
    {
        // The browser workbench: when <c>Spatial:WebRoot</c> points at the built
        // React application, the host serves it as static content — same origin, no
        // CORS, no desktop shell. The static middleware runs BEFORE routing so
        // default-file serving wins at the root.
        WorkbenchServing.Serve(app, configuration);
        app.UseRouting();

        app.MapOpenApi();

        app.MapGet("/", () => Results.Ok(new
        {
            name = "Spatial.Host",
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
            api = "/api",
            openapi = "/openapi/v1.json",
        }));

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/health/ready", () => Results.Ok(new ReadyResponse("ready", ["demo", "postgis"])));

        app.MapSpatialApi();

        // The Esri GeoServices boundary adapter (ADR-0035): mounted at
        // Spatial:GeoServices:Root, independent of the engine's own typed API.
        var geoServicesOptions = configuration.GetSection("Spatial:GeoServices").Get<GeoServicesOptions>()
            ?? new GeoServicesOptions();
        GeoServicesEndpoints.Map(app, geoServicesOptions);
    }
}

/// <summary>
/// Serves the built browser workbench from <c>Spatial:WebRoot</c> when set
/// and present on disk. Rooted at the configured directory so the API routes
/// stay untouched; the default read of the host identity at <c>/</c> is
/// shadowed by the app's <c>index.html</c> when the workbench is served.
/// </summary>
internal static class WorkbenchServing
{
    public static void Serve(WebApplication app, IConfiguration configuration)
    {
        var webRoot = configuration["Spatial:WebRoot"];
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return;
        }

        Mount(app, webRoot);
    }

    private static void Mount(WebApplication app, string webRoot)
    {
        if (!Directory.Exists(webRoot))
        {
            throw new InvalidOperationException(
                $"Spatial:WebRoot is set to '{webRoot}', but that directory does not exist.");
        }

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(webRoot) });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
    }
}
