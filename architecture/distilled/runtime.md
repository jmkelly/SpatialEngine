# Services: Interfaces, Implementations, Composition (distilled)

Covers `Spatial.PluginSdk` interfaces and their in-process implementations.
Implements ADR-0033 (replaces ADR-0002/0003/0006/0007/0008/0013/0022/0023/0024/0025
worker machinery).

## Service shape

A service = a plain C# interface over core types, implemented once per
provider and composed by `Spatial.Host` with Microsoft DI. No manifests, no
worker processes, no language-neutral wire, no jobs/resources/streams
infrastructure.

| Interface | Implementations | Behaviour |
| --- | --- | --- |
| `IGeometryOperations` | `NtsGeometryOperations` | Buffer, intersection, validate, simplify; pure, planar; invalid geometry = successful `false` |
| `ICrsDirectory` + `ICoordinateTransforms` | `ProjNetTransforms` | CRS describe + transform; x-first convention; curated EPSG catalogue |
| `IDataCatalogue` | `DemoStore`, `PostgisStore` | List (LIKE `pattern`), describe, create-from-batch |
| `IFeatureStore` | `DemoStore`, `PostgisStore` | Scan, bbox + attribute query, single-transaction write; reads return `FeatureBatch` pages |
| `ITransactionStore` | `PostgisStore` | String handles over open connections (`Begin/Commit/Rollback`) |
| `IDemoJobs` | `DemoStore` | Cancellable `SleepAsync` delay with `IProgress<double>` |

## Error model

One exception: `SpatialException` with a stable dotted `Code`.

| Code | Meaning | HTTP |
| --- | --- | --- |
| `invalid.arguments` | Bad arguments **and** inputs the algorithm cannot process | 400 |
| `not.found` | Unknown dataset/transaction | 404 |
| `store.unavailable` | No connection configuration or DB failure (redacted) | 503 |
| anything else | Provider failure | 500 |

## Interchange rules

- **Inline values**: points, envelopes, options, small values.
- **Base64 canonical binary** in typed DTOs: geometry as SGEOM, batches as
  SFBAT (ADR-0020). Equal values encode to identical bytes.
- **JSON** for metadata DTOs (`DatasetSummary`, `DatasetDescription`,
  `CrsDescription`) and the typed route envelopes.
- Long work is a plain cancellable `Task` (`CancellationToken`, optional
  `IProgress<double>`); the host never parks a request on a job.
- Secrets come from host configuration (`Spatial:Postgis:ConnectionString`
  or `SPATIAL_POSTGIS_CONNECTION`), never from request bodies.
- Handles are store-owned strings (transactions); the demo store is
  read-only and has none.

## Where tests live

Service behaviour tests live with each implementation
(`tests/unit/Spatial.*.Tests`); the host surface is covered by
`tests/integration/Spatial.Host.Tests` against the real host; store-backed
PostGIS behaviour by the containerised suite
(`tests/integration/Spatial.PostGIS.Tests`, Testcontainers, skips without
Docker).
