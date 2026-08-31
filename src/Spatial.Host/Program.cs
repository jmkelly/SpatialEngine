using System.Reflection;
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