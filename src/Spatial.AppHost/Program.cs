// Spatial Engine local development composition (ADR-0034): the
// PostGIS container, the host and the Vite workbench in one Aspire
// application. Connection strings and service URLs are injected here; the
// engine reads them from configuration/environment, never from the repo.

var builder = DistributedApplication.CreateBuilder(args);

// The PostGIS image matches the integration-test fixture so local data
// behaves like CI (tests/integration/Spatial.PostGIS.Tests).
var postgis = builder.AddPostgres("postgis")
    .WithImage("postgis/postgis")
    .WithImageTag("16-3.4");

var spatialDb = postgis.AddDatabase("spatial");

// The host reads SPATIAL_POSTGIS_CONNECTION (PostgisOptions), falling back
// to Spatial:Postgis:ConnectionString when the environment is unset.
var host = builder.AddProject("spatial-host", "../Spatial.Host/Spatial.Host.csproj")
    .WithEnvironment("SPATIAL_POSTGIS_CONNECTION", spatialDb);

// The Vite dev server proxies /api, /health and /openapi to the host
// (apps/workbench-web/vite.config.ts). Aspire assigns the host port, so the
// proxy target is passed explicitly rather than hard-coded.
builder.AddViteApp("workbench", "../../apps/workbench-web")
    .WithEnvironment("VITE_DEV_HOST", host.GetEndpoint("http"));

builder.Build().Run();
