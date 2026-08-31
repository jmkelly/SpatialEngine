using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Spatial.Host;
using Spatial.Host.Api;
using Spatial.PluginSdk.Http;

var builder = WebApplication.CreateBuilder(args);

// One JSON contract across the API: camelCase property names and enum
// members as their camelCase names, mirroring the SDK's HostApiJson options
// so the client SDK and the host cannot drift (ADR-0030).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = HostApiJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.DictionaryKeyPolicy = HostApiJson.Options.DictionaryKeyPolicy;
    foreach (var converter in HostApiJson.Options.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

// The OpenAPI description for the public HTTP contracts (plan §12).
builder.Services.AddOpenApi();

// Wire the runtime BEFORE the application serves: registry, capability
// runtime and — when a packages root is configured — the supervisor with
// every plugin package activated as an isolated worker. The host waits for
// its workers at startup (readiness), so the first request never races the
// plugin handshake.
using var startupLogger = LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    logging.AddConsole();
});
var runtime = await SpatialHostRuntime.CreateAsync(builder.Configuration, startupLogger);
builder.Services.AddSingleton(runtime);

var app = builder.Build();

// The browser workbench (Phase 10, ADR-0031): when <c>Spatial:WebRoot</c>
// points at the built React application, the host serves it as static
// content so the workbench runs in a normal browser from the independently
// executable host — same origin, no CORS, no desktop shell. Without the
// setting the host serves only the API (its default, tested surface). The
// static middleware runs BEFORE routing so default-file serving wins at the
// root: ASP.NET's static-file middleware refuses to serve a path that
// routing has already matched, and the host identity endpoint matches
// <c>/</c>.
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

app.MapGet("/health/ready", (SpatialHostRuntime host) => Results.Ok(new
{
    status = "ready",
    plugins = host.Supervisor?.Workers.Count ?? 0,
}));

app.MapSpatialApi();

app.Run();

/// <summary>Entry point type used by <c>WebApplicationFactory</c> in integration tests.</summary>
public partial class Program;

/// <summary>
/// Serves the built browser workbench from <c>Spatial:WebRoot</c> when set
/// and present on disk (the Phase 10 single-process browser demo, ADR-0031).
/// Rooted at the configured directory so the API routes stay untouched; the
/// default read of the host identity at <c>/</c> is shadowed by the app's
/// <c>index.html</c> when the workbench is served.
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
