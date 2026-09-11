# Composition: Interfaces, Implementations, Lifecycle (distilled)

Implements ADR-0033 (replaces ADR-0006/0013/0025 worker boundaries).

## Composition model

Implementations are linked .NET projects composed by `Spatial.Host` with
Microsoft DI — keyed services where two stores serve one contract:

- `IDataCatalogue`/`IFeatureStore` keyed `"demo"` (`DemoStore`, always
  available) and `"postgis"` (`PostgisStore`, needs a connection string).
- `ITransactionStore` keyed `"postgis"` only; the demo store is read-only.
- `IGeometryOperations`, `ICrsDirectory`, `ICoordinateTransforms`,
  `IDemoJobs` as singletons.

In order of preference for new implementations:

1. **In-process implementation** (default): a project referencing Core +
   SDK only, registered in `Program.cs`.
2. **External service**: only behind a new narrow interface with an ADR.

## Project rules

- `Spatial.Core` — values only, zero packages, zero references.
- `Spatial.PluginSdk` — interfaces + DTOs over core types only, zero
  packages, only a Core reference.
- Implementations (`Spatial.Operations.*`, `Spatial.Transformations.*`,
  `Spatial.Provider.*`) — reference Core + SDK only; third-party packages
  (NTS, ProjNET, Npgsql) stay inside the owning implementation (ADR-0005).
- `Spatial.Host` — the only project that references implementations.

## Lifecycle

Processes are the host's own: start, serve, stop. There are no worker
processes, no health-check/restart supervision, no side-by-side versions,
no drain/rollback. Restart-to-upgrade is the accepted replacement story.
Plugin code is fully trusted; persistent state is external (the database).
