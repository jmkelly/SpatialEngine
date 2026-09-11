using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Spatial.Host.Api;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.Provider.Demo;
using Spatial.Provider.PostGIS;
using Spatial.Transformations.ProjNet;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ProjNetTransforms>();
builder.Services.AddSingleton<ICrsDirectory>(services => services.GetRequiredService<ProjNetTransforms>());
builder.Services.AddSingleton<ICoordinateTransforms>(services => services.GetRequiredService<ProjNetTransforms>());
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddKeyedSingleton<IDataCatalogue, DemoStore>("demo");
builder.Services.AddKeyedSingleton<IFeatureStore, DemoStore>("demo");
builder.Services.AddSingleton<IDemoJobs>(services => services.GetRequiredService<DemoStore>());
builder.Services.AddSingleton<PostgisStore>();
builder.Services.AddKeyedSingleton<IDataCatalogue, PostgisStore>("postgis");
builder.Services.AddKeyedSingleton<IFeatureStore, PostgisStore>("postgis");
builder.Services.AddKeyedSingleton<ITransactionStore, PostgisStore>("postgis");

var app = builder.Build();

// The browser workbench: when <c>Spatial:WebRoot</c> points at the built
// React application, the host serves it as static content — same origin, no
// CORS, no desktop shell. The static middleware runs BEFORE routing so
// default-file serving wins at the root.
WorkbenchServing.Serve(app, builder.Configuration);
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

app.Run();

/// <summary>Entry point type used by <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;

internal sealed record ReadyResponse(string Status, IReadOnlyList<string> Stores);

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
