# Composition: Interfaces, Implementations, Lifecycle (distilled)

Implements ADR-0033 (replaces ADR-0006/0013/0025 worker boundaries).

## Composition model

Implementations are linked .NET projects composed by `Spatial.Host` with
Microsoft DI — keyed services where two stores serve one contract:

- `IDataCatalogue`/`IFeatureStore` keyed `"demo"` (`DemoStore`, always
  available), `"postgis"` (`PostgisStore`, needs a connection string) and
  one key per configured ArcGIS REST service (`ArcGisRestStore`, ADR-0035).
- `IFeatureEditStore` keyed `"postgis"` only (ADR-0037); the demo and
  ArcGIS REST stores are read-only and do not implement it.
- `IFeatureLookup` keyed `"postgis"` only (ADR-0038), resolving the canonical
  `PostgisStore`; the demo and ArcGIS REST stores do not implement it and
  callers fall back to the scan.
- `ITransactionStore` keyed `"postgis"` only; the demo and ArcGIS stores are
  read-only.
- `IGeometryOperations`, `IGeometryMeasures`, `IGeometryProcessing`,
  `IGeometryRelations`, `ICrsDirectory`, `ICoordinateTransforms`,
  `IDemoJobs`, `IMapRenderer`, `IRasterOperations`, `ITileScheme` and
  `ITileCache` as singletons (ADR-0044/ADR-0046). `ITileScheme` and
  `ITileCache` are the pluggable tiling seams: new projections/cache owners
  are additional registrations, not host changes. `IRasterCatalogue` is
  keyed `"raster"` (ADR-0051), configured from `Spatial:Raster` and
  implemented by `Spatial.Imagery.Vips`.
- `Spatial.Adapter.GeoServices` is mounted by the host at
  `Spatial:GeoServices:Root` (ADR-0035).
- `IStoreRegistry` (`Spatial.Host`'s `KeyedStoreRegistry`) is the single
  typed seam over the keyed stores: a request's store name resolves to its
  catalogue, feature store and additive faces, so the boundary adapters and
  the host API depend on the registry and never hold `IServiceProvider`
  (ADR-0033). The container is captured only in this one implementation,
  where the runtime key is needed.

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
  `Spatial.Provider.*`, `Spatial.Rendering.*`, `Spatial.Imagery.*`,
  `Spatial.Tiling.*`) — reference Core + SDK only; third-party packages
  (NTS, ProjNET, Npgsql, SkiaSharp, NetVips) stay inside the owning
  implementation (ADR-0005).
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
