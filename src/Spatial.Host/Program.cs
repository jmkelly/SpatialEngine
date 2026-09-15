using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Serilog.Events;
using Spatial.Adapter.GeoServices;
using Spatial.Host;
using Spatial.Host.Api;
using Spatial.Imagery.Vips;
using Spatial.Maps;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;
using Spatial.Rendering.Skia;
using Spatial.Stores.ArcGisRest;
using Spatial.Stores.Demo;
using Spatial.Stores.Memory;
using Spatial.Stores.PostGIS;
using Spatial.Transformations.ProjNet;

var builder = WebApplication.CreateBuilder(args);
HostComposition.ConfigureServices(builder);

var app = builder.Build();
HostComposition.ConfigurePipeline(app, builder.Configuration);
StartupLogging.Log(app);

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
    /// <summary>Registers the in-process spatial services, configuration and logging.</summary>
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // Structured logging is installed first so composition itself is observable (ADR-0045).
        LoggingSetup.Configure(builder);

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

        EngineServices.Configure(builder);
        StoreServices.Configure(builder);
        MapServices.Configure(builder);
        RenderingServices.Configure(builder);
        OgcServices.Configure(builder);
    }

    /// <summary>Builds the request pipeline: request logging, workbench, routing, health, API and GeoServices.</summary>
    public static void ConfigurePipeline(WebApplication app, IConfiguration configuration)
    {
        // One structured event per request (ADR-0045); server errors are warnings.
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
        });

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
            routes = "/routes",
        }));

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/health/ready", () => Results.Ok(new ReadyResponse("ready", ["demo", "memory", "postgis"])));

        var adminOptions = AdminOptions.FromConfiguration(configuration);
        var ingestOptions = IngestOptions.FromConfiguration(configuration);
        app.MapSpatialApi(adminOptions, ingestOptions);

        // The Esri GeoServices boundary adapter (ADR-0035): mounted at
        // Spatial:GeoServices:Root, independent of the engine's own typed API.
        var geoServicesOptions = configuration.GetSection("Spatial:GeoServices").Get<GeoServicesOptions>()
            ?? new GeoServicesOptions();
        var maps = app.Services.GetRequiredService<IMapRegistry>();
        GeoServicesEndpoints.Map(app, geoServicesOptions, maps);
        EsriAdminEndpoints.Map(app, new EsriAdminOptions
        {
            Root = configuration["Spatial:GeoServices:AdminRoot"] ?? "/arcgis/admin",
            Token = adminOptions.Token,
            MaxBytes = ingestOptions.MaxBytes,
            MaxFeatures = ingestOptions.MaxFeatures,
            BatchSize = ingestOptions.BatchSize,
        }, maps);

        // The OGC WMS/WFS boundary adapter (ADR-0053 §3): mounted at
        // Spatial:Ogc:Root, projecting the same maps read-only.
        OgcServices.Map(app);

        // The discovery page (ADR-0018): a live HTML index of every mounted
        // route and every published map. Mapped last so it reports the whole
        // routing table.
        DiscoveryPage.Map(app);
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
