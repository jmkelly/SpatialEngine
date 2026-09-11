# Spatial.PluginSdk

Public service **interfaces** and DTOs for spatial implementations and
clients (ADR-0033). Interfaces ship here; implementation projects implement
them; `Spatial.Host` composes everything with Microsoft DI. Referenced by
implementations and the host — never the reverse.

## Owned here

- `SpatialException` — the single error type (`invalid.arguments`,
  `not.found`, `store.unavailable`).
- `IGeometryOperations` — buffer, intersection, validate, simplify over
  core geometry values.
- `ICrsDirectory` + `ICoordinateTransforms` — CRS description and
  coordinate transformation (x-first convention).
- `BoundingBox`, `IDataCatalogue`, `IFeatureStore`, `IFeatureLookup`,
  `ITransactionStore` —
  dataset catalogue, feature reads/writes (canonical `FeatureBatch` pages),
  additive read-by-identity lookup for edit resolution (ADR-0038), and
  store-owned string transaction handles.
- `IDemoJobs` — the demo cancellable sleep with progress.
- `Providers/DatasetSummary`, `Providers/DatasetDescription` — catalogue DTOs.
- `Transformations/CrsDescription` (+ axes, ellipsoid, kind, identity) —
  CRS metadata DTOs.
- `Http/` — the typed host API shapes (`SpatialHttpContracts`) plus the
  shared `HostApiJson` options and the `FeatureSchema`/`FieldDefinition`
  JSON converters.

## Rules

- Public contracts carry only `Spatial.Core` types (ADR-0005).
- No packages, only a `Spatial.Core` reference. New behaviour lands with
  SDK + implementation + tests + ADR together; versions live in
  `Directory.Packages.props`. NuGet creation is a later milestone — do not
  add packing metadata yet.
