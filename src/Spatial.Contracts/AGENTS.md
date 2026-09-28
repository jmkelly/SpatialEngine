# Spatial.Contracts

Public service **interfaces** and DTOs for spatial implementations and
clients (ADR-0033). Interfaces ship here; implementation projects implement
them; `Spatial.Host` composes everything with Microsoft DI. Referenced by
implementations and the host — never the reverse.

## Owned here

- `SpatialException` — the single error type (`invalid.arguments`,
  `not.found`, `store.unavailable`, `auth.failed`, `auth.unauthorized`,
  `auth.forbidden`).
- `IAuthService` + `AuthIdentity`/`AuthToken`/`AuthGuard` — the package-free
  phase-1 authentication and role-enforcement contract (ADR-0071).
- `IGeometryOperations` — buffer, intersection, validate, simplify over
  core geometry values.
- `ICrsDirectory` + `ICoordinateTransforms` — CRS description and
  coordinate transformation (x-first convention).
- `BoundingBox`, `IDataCatalogue`, `IFeatureStore`, `IFeatureEditStore`,
  `IFeatureLookup`, `ITransactionStore`, and the optional
  `IVersionedFeatureStore` + `ContentVersions` content-version vocabulary —
  dataset catalogue, feature reads/writes (canonical `FeatureBatch` pages),
  additive per-feature editing (ADR-0037) and read-by-identity lookup for
  edit resolution (ADR-0038), a per-dataset content version a derived cache
  such as the tile cache keys on (ADR-0075), additive per-feature attachment
  blobs (ADR-0065), and store-owned string transaction handles.
- `IMapRegistry` + `Map`/`MapServiceKind`/`MapLayer`/`MapLayerKind`
  — the runtime map registry (ADR-0053), the neutral unit of authoring and
  exposure (name, store, stable-id layers, enabled services). A map exposes
  any subset of Feature/Map/Tiles/Wms/Wfs/Image; the same dataset can be
  styled differently in different maps.
- `LayerRelationship`/`LayerRelationshipCardinality`/`LayerRelationshipJoin`
  — a map layer's declared relationships to the map's other layers
  (ADR-0077): two key columns plus a cardinality, with a join dataset for
  many-to-many. The declaration is publication state, so it lives on
  `MapLayer` and never in a store.
- `IDatasetIngest` + `IngestRequest`/`IngestOutcome`/`IngestIdentity` —
  atomic bulk create-and-load with an identity mode (ADR-0041).
- `Providers/IStoreRegistry` — the typed seam over the keyed stores
  (ADR-0033): a runtime store name resolves to its catalogue, feature store
  and additive faces, so boundary adapters and the host API never hold the
  DI container. Implemented by `Spatial.Host`'s `KeyedStoreRegistry`.
- `IDemoWork` — the demo cancellable sleep with progress.
- `ITileScheme` + `TileCoordinate`/`TileLevel` and `ITileCache` +
  `TileCacheKey` — pluggable tiling schemes and a content-addressed tile
  cache for raster and MVT bytes (ADR-0046/0070); Web-Mercator, MVT and
  the host caches are implementations.
- `IVectorTileService` + `VectorTileRequest`/`VectorTileLayer`/`VectorTile`
  — core-typed MVT feature-layer seam; protobuf and encoder types stay in
  `Spatial.Tiling.Mvt` (ADR-0070).
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
