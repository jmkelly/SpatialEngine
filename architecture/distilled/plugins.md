# Composition: Interfaces, Implementations, Lifecycle (distilled)

Implements ADR-0033 (replaces ADR-0006/0013/0025 worker boundaries).

## Composition model

Implementations are linked .NET projects composed by `Spatial.Host` with
Microsoft DI — keyed services where two stores serve one contract:

- `IDataCatalogue`/`IFeatureStore` keyed `"demo"` (`DemoStore`, always
  available), `"postgis"` (`PostgisStore`, needs a connection string) and
  one key per configured ArcGIS REST service (`ArcGisRestStore`, ADR-0035).
- `ITransactionStore` keyed `"postgis"` only; the demo and ArcGIS stores are
  read-only.
- `IGeometryOperations`, `IGeometryMeasures`, `IGeometryProcessing`,
  `IGeometryRelations`, `ICrsDirectory`, `ICoordinateTransforms`,
  `IDemoJobs` as singletons.
- `Spatial.Adapter.GeoServices` is mounted by the host at
  `Spatial:GeoServices:Root` (ADR-0035).

In order of preference for new implementations:

1. **In-process implementation** (default): a project referencing Core +
   SDK only, registered in `Program.cs`.
2. **External service**: only behind a new narrow interface with an ADR.

## Project rules

- `Spatial.Core` — values only, zero packages, zero references.
- `Spatial.PluginSdk` — interfaces + DTOs over core types only, zero
  packages, only a Core reference.
- `Spatial.Interop.Esri` — the shared Esri wire codec: Core only, no NTS,
  ASP.NET or HttpClient (ADR-0035).
- Implementations (`Spatial.Operations.*`, `Spatial.Transformations.*`,
  `Spatial.Provider.*`) — reference Core + SDK only; third-party packages
  (NTS, ProjNET, Npgsql) stay inside the owning implementation (ADR-0005).
- Boundary projects (`Spatial.Adapter.GeoServices`,
  `Spatial.Provider.ArcGisRest`) additionally reference only
  `Spatial.Interop.Esri`; the adapter owns ASP.NET Core, the provider owns
  `HttpClient` (ADR-0035).
- `Spatial.Host` — the only project that references implementations.

## Lifecycle

Processes are the host's own: start, serve, stop. There are no worker
processes, no health-check/restart supervision, no side-by-side versions,
no drain/rollback. Restart-to-upgrade is the accepted replacement story.
Plugin code is fully trusted; persistent state is external (the database).
