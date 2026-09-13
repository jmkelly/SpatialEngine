// Spatial Engine local development composition (ADR-0034): the
// PostGIS container, the Seq log server (ADR-0045), the host and the Vite
// workbench in one Aspire application. Connection strings and service URLs
// are injected here; the engine reads them from configuration/environment,
// never from the repo.

var builder = DistributedApplication.CreateBuilder(args);

// Opt-in remote development access (e.g. over a private Tailscale network).
// SPATIAL_DEV_BIND (usually 0.0.0.0) publishes the host project and the Vite
// dev server on that interface instead of loopback; VITE_ALLOWED_HOSTS is an
// optional comma-separated Host-header allow-list for the dev server (Vite
// rejects unknown hostnames). Unset, the composition binds loopback as before.
var devBind = Environment.GetEnvironmentVariable("SPATIAL_DEV_BIND");
var devAllowedHosts = Environment.GetEnvironmentVariable("VITE_ALLOWED_HOSTS");

// The PostGIS image matches the integration-test fixture so local data
// behaves like CI (tests/integration/Spatial.PostGIS.Tests).
var postgis = builder.AddPostgres("postgis")
    .WithImage("postgis/postgis")
    .WithImageTag("16-3.4");

var spatialDb = postgis.AddDatabase("spatial")
    // The postgis image loads its extension into the bootstrap database and
    // the template_postgis template only; Aspire creates 'spatial' after the
    // container is ready, so it must be cloned from the template to carry the
    // postgis extension (the integration-test fixture does the equivalent
    // with CREATE EXTENSION postgis). Without it, ingesting geometry fails
    // with 42704 'type "geometry" does not exist'.
    .WithCreationScript("CREATE DATABASE \"spatial\" TEMPLATE template_postgis");

// Seq is the local structured-log server (ADR-0045); the host is pointed at
// its HTTP endpoint so Serilog can ship events to it. The container is a
// development convenience — the host runs without it (ADR-0018).
var seq = builder.AddSeq("seq");

// The host reads SPATIAL_POSTGIS_CONNECTION (PostgisOptions), falling back
// to Spatial:Postgis:ConnectionString when the environment is unset, and
// SPATIAL_SEQ_URL for the Seq sink.
var host = builder.AddProject("spatial-host", "../Spatial.Host/Spatial.Host.csproj")
    .WithEnvironment("SPATIAL_POSTGIS_CONNECTION", spatialDb)
    .WithEnvironment("SPATIAL_SEQ_URL", seq.GetEndpoint("http"));

if (!string.IsNullOrWhiteSpace(devBind))
{
    // Remote development access (e.g. over a private Tailscale network):
    // pin stable ports and serve proxy-less so each service is reachable
    // directly at http://<tailnet-host>:<port>. By default Aspire fronts
    // executables with a DCP proxy on a loopback-only, dynamically allocated
    // port — the URL the dashboard advertises — which is unreachable from a
    // remote browser and different on every run. Setting Port/TargetPort
    // together keeps the allocation stable (Aspire does not derive one from
    // the other); IsProxied = false serves the endpoint directly instead of
    // the loopback proxy. VITE_DEV_HOST resolves to the pinned host
    // endpoint, so the Vite /api proxy tracks it automatically.
    host.WithEndpoint("http", endpoint =>
    {
        endpoint.TargetHost = devBind;
        endpoint.Port = 5199;
        endpoint.TargetPort = 5199;
        endpoint.IsProxied = false;
    });
}

// The Vite dev server proxies /api, /health and /openapi to the host
// (apps/workbench-web/vite.config.ts). Aspire assigns the host port, so the
// proxy target is passed explicitly rather than hard-coded.
var workbench = builder.AddViteApp("workbench", "../../apps/workbench-web")
    .WithEnvironment("VITE_DEV_HOST", host.GetEndpoint("http"));

// Aspire launches JS apps with NODE_ENV=production, but `vite dev` needs a
// development environment: with production, plugin-react skips the Fast
// Refresh preamble while the bundles still reference it, so the page loads
// (200) yet stays blank ($RefreshSig$ is not defined). This applies to the
// normal local run too, not just remote access.
workbench.WithEnvironment("NODE_ENV", "development");

if (!string.IsNullOrWhiteSpace(devBind))
{
    workbench.WithEnvironment("VITE_DEV_BIND", devBind);

    // Proxy-less with a pinned port: Vite itself listens on 0.0.0.0:5273
    // (Aspire passes --port from the target port; VITE_DEV_BIND sets the
    // interface via vite.config.ts), so http://<tailnet-host>:5273 is stable
    // across restarts and reachable without the dashboard's loopback proxy.
    workbench.WithEndpoint("http", endpoint =>
    {
        endpoint.TargetHost = devBind;
        endpoint.Port = 5273;
        endpoint.TargetPort = 5273;
        endpoint.IsProxied = false;
    });
}

if (!string.IsNullOrWhiteSpace(devAllowedHosts))
{
    workbench.WithEnvironment("VITE_ALLOWED_HOSTS", devAllowedHosts);
}

builder.Build().Run();
