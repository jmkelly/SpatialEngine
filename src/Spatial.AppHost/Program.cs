// Spatial Engine local development composition (ADR-0034): the
// PostGIS container, the Seq log server (ADR-0045), the host and the Vite
// workbench in one Aspire application. Connection strings and service URLs
// are injected here; the engine reads them from configuration/environment,
// never from the repo.

var builder = DistributedApplication.CreateBuilder(args);

// The PostGIS image matches the integration-test fixture so local data
// behaves like CI (tests/integration/Spatial.PostGIS.Tests).
var postgis = builder.AddPostgres("postgis")
    .WithImage("postgis/postgis")
    .WithImageTag("16-3.4");

var spatialDb = postgis.AddDatabase("spatial");

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

// The Vite dev server proxies /api, /health and /openapi to the host
// (apps/workbench-web/vite.config.ts). Aspire assigns the host port, so the
// proxy target is passed explicitly rather than hard-coded.
builder.AddViteApp("workbench", "../../apps/workbench-web")
    .WithEnvironment("VITE_DEV_HOST", host.GetEndpoint("http"));

builder.Build().Run();
