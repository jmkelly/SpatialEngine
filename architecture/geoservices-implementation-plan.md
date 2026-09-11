# GeoServices REST Implementation Plan

> **Status:** proposed. Companion to
> `architecture/decisions/ADR-0035-geoservices-rest-boundary-adapter.md`.
> This is a focused plan for two tracks — **serving** the Esri GeoServices
> REST Specification from the engine and **consuming** ArcGIS REST as a data
> provider. Where it disagrees with `architecture/implementation-plan.md`,
> the ADRs and this plan govern the GeoServices work; everything else in the
> main plan still applies.
>
> **Specification baseline:** Esri GeoServices REST Specification v1.0
> (`architecture/references/geoservices-rest-spec.pdf`).
> **Compatibility review:** `architecture/references/geoservices-compatibility.md`
> (read it first — it catalogues today's gaps).

## 1. Goal and non-goals

**Goal.** Let the engine meet the Esri ecosystem at two edges without
polluting the core:

- **Track S — serve:** an Esri client can point at
  `https://<host>/arcgis/rest/services/...` and read the engine's
  geometry and features using standard GeoServices REST requests.
- **Track C — consume:** the engine can register a remote ArcGIS REST
  service as a normal store and read it through `IDataCatalogue` /
  `IFeatureStore`.

**Non-goals (standing, from principles 1–2 and plan §4.2):**

- No map rendering, tiling or `export` (the engine is headless; MapLibre
  renders client-side).
- No raster (`ImageServer`), geocoding (`GeocodeServer`) or geoprocessing
  (`GPServer`) — no renderer, raster pipeline or job model exists.
- No arbitrary SQL passthrough, no server-side fetch of caller-supplied
  URLs, no credentials in request bodies.
- No change to `Spatial.Core`, `Spatial.PluginSdk` public HTTP contracts or
  the existing SDKs, except the deliberate operation-verb additions in
  Phase S1b.

## 2. Tracks and phase map

| Track | Phase | Deliverable | Gate |
| --- | --- | --- | --- |
| S | S0 | Interop codec, CRS map, error envelope, catalog resource | — |
| S | S1a | Geometry Service over existing verbs (`project`, `generalize`, `intersect`, `buffer`) | S0 |
| S | S1b | New geometry verbs + remaining Geometry Service operations | contract extension |
| S | S2 | Read-only Feature Service (service/layer metadata, `query`) | S0 |
| S | S3 | Feature editing (`add`/`update`/`delete`/`applyEdits`) | follow-up ADR, S2 |
| C | C0 | Provider skeleton, config, catalogue/describe | — |
| C | C1 | Scan/query with pagination and geometry conversion | C0, S0 codec |
| C | C2 | `where` → parameterised filter translation, field pushdown | C1 |

Track S delivers the engine as a GeoServices endpoint; Track C delivers
ArcGIS REST as a data source. They share only `Spatial.Interop.Esri`.

## 3. Baseline and version delta

Target **v1.0** as the correctness baseline (it is the held spec). Record,
but do not implement, the additions modern clients expect so the design is
future-proof:

| Area | v1.0 (baseline) | 10.x additions to accommodate |
| --- | --- | --- |
| Query | `objectIds`, `where`, `geometry`+`geometryType`, `inSR`, `spatialRel`, `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`, `time` | `orderByFields`, `resultOffset`/`resultRecordCount`, `returnCountOnly`, `returnExtentOnly`, `returnDistinctValues`, `groupByFieldsForStatistics`, `outStatistics`, `returnZ`/`returnM` |
| Layer | `objectIdField`, `fields`, `geometryType`, `capabilities` | `hasZ`/`hasM`, `maxRecordCount`, `supportedQueryFormats`, `advancedQueryCapabilities`, `syncEnabled` |
| Geometry | point/polyline/polygon/multipoint/envelope | `hasZ`/`hasM` flags on geometry objects |
| Edits | `addFeatures`/`updateFeatures`/`deleteFeatures`/`applyEdits` | `rollbackOnFailure`, `useGlobalIds`, `returnEditResults` variants |

The facade must reject unknown parameters explicitly (or ignore them with a
documented note) rather than silently mis-handling them.

## 4. Architecture and boundaries

```text
Spatial.Core ──values──┐
                       ├── Spatial.Interop.Esri (JSON codec, WKID map, error model)
Spatial.PluginSdk ─────┤        ▲                    ▲
  (contracts)          │        │                    │
                       │  Spatial.Adapter.GeoServices   Spatial.Provider.ArcGisRest
                       │        │ (serve)              │ (consume)
                       └────────┴──────────┬───────────┘
Spatial.Operations.NetTopologySuite ◄──────┤ (new verbs, S1b)
Spatial.Transformations.ProjNet ◄──────────┤
Spatial.Provider.PostGIS / Demo ◄──────────┘
                       Spatial.Host (mounts adapter route group; registers provider in DI)
```

Rules:

- `Spatial.Interop.Esri` references `Spatial.Core` only. No NTS, no
  ASP.NET, no `HttpClient`. It is the single place Esri JSON shapes live.
- `Spatial.Adapter.GeoServices` references `Spatial.PluginSdk`,
  `Spatial.Interop.Esri` and ASP.NET Core. It contains **no algorithms**.
- `Spatial.Provider.ArcGisRest` references `Spatial.PluginSdk`,
  `Spatial.Interop.Esri` and `System.Net.Http`. It contains no algorithms.
- `Spatial.Host` mounts the adapter route group and registers the provider;
  neither is referenced by core/SDK/other implementations.
- The canonical codecs and the engine host API remain untouched.

### Host mounting and configuration

```jsonc
"Spatial": {
  "GeoServices": {
    "Root": "/arcgis/rest/services",          // Esri-conventional prefix
    "Services": [
      { "name": "demo",   "store": "demo",   "type": "FeatureServer" },
      { "name": "cities", "store": "postgis", "type": "FeatureServer" }
    ]
  },
  "ArcGisRest": {
    "Services": [
      { "name": "remote", "url": "https://example.com/arcgis/rest/services/x/FeatureServer" }
    ],
    "Token": ""                                 // host-config secret only
  }
}
```

`store` selects the keyed engine store (`demo`/`postgis`). A logical
service expands to a `FeatureServer` root whose layer ids map to that
store's datasets. **Open question (S2):** layer id assignment must be
deterministic — propose ordering datasets by id and assigning stable
indices, cached with the catalogue listing.

## 5. Track S — serving GeoServices

### S0 — Foundations

- Create `Spatial.Interop.Esri`: geometry codec (point `{x,y}`, polyline
  `{paths}`, polygon `{rings}`, multipoint `{points}`, envelope
  `{xmin..}`; plus the comma-separated point/envelope shorthand and the
  `spatialReference` object), `esriFieldType*` ↔ `AttributeKind`, Esri
  error `{error:{code,message,details}}`, WKID ↔ `EPSG:` map.
- Reject the `{"url": ...}` input form (SSRF) with a typed error.
- Create `Spatial.Adapter.GeoServices` with a route group mounted at
  `Spatial:GeoServices:Root`; implement `f` negotiation — `json` only;
  any other value returns a documented "format not supported" error.
- Implement the **Catalog** resource (spec §3): `services[]` from the
  configured services.

**Proof:** codec round-trip tests seeded from the spec's JSON examples;
an architecture test that `Spatial.Interop.Esri` has no NTS/HTTP deps.

### S1a — Geometry Service over existing verbs

Implement the Geometry Service resource (§7.0.1) and the operations the
engine can already satisfy, mapping — never by name alone:

| GeoServices | Engine verb | Note |
| --- | --- | --- |
| `project` | `ICoordinateTransforms.Transform` | loop over the `geometries` array; `inSR`/`outSR` → `source`/`target` |
| `generalize` | `IGeometryOperations.Simplify` | Douglas-Peucker — correct per spec §7.0.13 |
| `intersect` | `IGeometryOperations.Intersection` | fold the array against the single geometry; return the array shape |
| `buffer` | `IGeometryOperations.Buffer` | **planar, single distance**; reject `unit`/`bufferSR`/multi-`distances` until S1b, or accept only matching CRS and no unit |

Always package input and output geometry as **arrays** (§7.0.4.2).

**Proof:** conformance fixtures for each op using the spec's example
payloads; an HTTP test that `simplify` is *not* mapped to `Simplify` (the
semantic trap) until S1b provides repair.

### S1b — New geometry verbs

Extend `Spatial.PluginSdk` operation interfaces and implement in
`Spatial.Operations.NetTopologySuite`, in dependency order:

1. `generalize` is already covered; add `simplify`-as-repair (OGC
   MakeValid-equivalent) as a distinct verb — the name trap must be
   resolved by two distinct engine verbs.
2. Measurements: area/perimeter, length, distance, label point.
3. Set ops: `union`, `difference`.
4. Constructors: `densify`, `convexHull`, `offset`, `cut`, `reshape`,
   `trimExtend`, `autoComplete`.
5. Predicates for `relation` (DE-9IM `relationParam`) and the `spatialRel`
   family, if S2 needs more than envelope-intersects.

Interface granularity (one extended `IGeometryOperations` vs
`IGeometryProcessing` + `IGeometryMeasures` + `IGeometryRelations`) is an
open question for the implementing change; the rule is that the adapter
maps and the operations project computes.

**Proof:** unit tests per verb; conformance fixtures from spec §7; the
`simplify`-repair fixture with the spec's "one ring → two rings" example.

### S2 — Read-only Feature Service

- `FeatureServer` root (§9.0): `layers[]`/`tables[]` derived from the
  configured store's catalogue (`IDataCatalogue.ListAsync`).
- Layer resource (§9.1): `id`, `name`, `type`, `geometryType`,
  `objectIdField`, `fields[]` (`esriFieldType*`), `spatialReference`,
  `capabilities` (read-only value until S3).
- `query` (§9.1.4): support `objectIds`, `where` (safe subset), `geometry`
  + `geometryType` + `inSR`, `spatialRel` (envelope-intersects first),
  `outFields`, `returnGeometry`, `outSR`, `returnIdsOnly`.
  - `where` → the engine's parameterised filter grammar via a translator;
    unsupported constructs → `invalid.arguments`.
  - `outSR` → `ICoordinateTransforms.Transform`.
  - `returnIdsOnly` → feature id list.
  - Response shape: `{objectIdFieldName, geometryType, spatialReference,
    fields[], features[]}` per spec §9.1.4.3.
- Reject `queryRelatedRecords` (no relationship model) with a typed
  "not supported" error.

**Proof:** facade-over-demo-store integration tests driven over HTTP;
fixtures matching the spec's earthquake query example.

### S3 — Editing (gated)

Requires a follow-up ADR extending `IFeatureStore` with update and delete
(and corresponding PostGIS SQL) plus per-feature edit results
(`{objectId, globalId, success, error:{code,description}}`). Until then the
facade advertises read-only capabilities and rejects edit routes.

### Explicitly out of scope for Track S

`MapServer` (export/identify/find/tiles), `ImageServer`, `GeocodeServer`,
`GPServer`. The workbench continues to consume Esri basemap tiles as a
client, which needs no engine work.

## 6. Track C — consuming ArcGIS REST

### C0 — Provider skeleton

- Create `Spatial.Provider.ArcGisRest` implementing `IDataCatalogue`
  (`ListAsync`/`DescribeAsync`) against a configured FeatureServer or
  MapServer URL.
- Map layers/tables → `DatasetSummary` / `DatasetDescription`:
  `esriFieldType*` → `AttributeKind`, `spatialReference.wkid` → `EPSG:`
  via the shared map, `objectIdField` → identity column.
- `CreateAsync` is not supported (typed `invalid.arguments`).

### C1 — Reads

- `ScanAsync`/`QueryAsync` issue `/query` with `f=json`, following
  pagination (`resultOffset`/`resultRecordCount`, `exceededTransferLimit`)
  and reassembling `FeatureBatch` pages.
- Convert Esri JSON geometry → core geometry inside the provider
  (ADR-0005 spirit) via `Spatial.Interop.Esri`.
- `objectId` → `FeatureId` (string form); stable for the dataset.
- Bbox → `geometry` envelope + `inSR`; `outSR` when the engine CRS differs.

### C2 — Filter and field pushdown

- Translate the engine filter grammar → `where` for the supported subset;
  otherwise fail `invalid.arguments` (never widen the query silently).
- Push `outFields`/`returnGeometry` to reduce transfer.
- Concurrency/cancellation: caller `CancellationToken` flows to the HTTP
  request; a cancelled query cancels the remote call.

**Proof:** provider tests use the Track S facade as the stub server
(ADR-0035 §8) — the same conformance fixtures, run in both directions.

## 7. Cross-cutting concerns

| Concern | Decision |
| --- | --- |
| Security — query | Facade never forwards raw `where`; closed grammar, bound parameters. Provider translates, never widens. |
| Security — SSRF | The `{"url": ...}` input form is rejected. Provider fetches only its configured base URL. |
| Security — secrets | ArcGIS token is host config (`Spatial:ArcGisRest:Token`), redaction-tested like the PostGIS connection string; never in request bodies or errors. |
| CRS | Curated WKID ↔ `EPSG:` map in `Spatial.Interop.Esri`; EPSG-only core identity (ADR-0009) unchanged; x-first preserved; unknown codes rejected. |
| Units | GeoServices `buffer` `unit`/`bufferSR` need projection or angular handling; until S1b, reject unsupported unit/CRS combinations explicitly. |
| Errors (serve) | `SpatialException` → Esri `{error:{code,message,details}}`; HTTP status stays consistent with the engine mapping (400/404/503/499). |
| Errors (consume) | Remote Esri error → `SpatialException` (`invalid.arguments`/`not.found`/`store.unavailable`); remote unreachable → `store.unavailable`. |
| Limits | Adapter enforces payload/time limits; provider caps page size. |
| Observability | No secrets in logs; facade routes log method+route+status only. |

## 8. Work packages

| # | Package | Depends on | Proof |
| --- | --- | --- | --- |
| 1 | `Spatial.Interop.Esri` geometry + feature codec | — | unit round-trip + spec examples |
| 2 | WKID ↔ EPSG map + `f=json` + error envelope | 1 | unit + HTTP error tests |
| 3 | Adapter project + catalog + Geometry Service shell | 1–2 | HTTP tests over demo store |
| 4 | S1a ops (`project`, `generalize`, `intersect`, `buffer`) | 3 | conformance fixtures |
| 5 | Operation contract extension ADR + new verbs | 4 | unit + conformance per verb |
| 6 | S1b remaining Geometry Service ops | 5 | spec §7 fixtures |
| 7 | S2 FeatureServer/layer/query | 3 | HTTP + query fixtures |
| 8 | S3 edit ADR + store update/delete | 7 | integration + edit-result fixtures |
| 9 | `Spatial.Provider.ArcGisRest` catalogue/describe | 1 | provider unit tests |
| 10 | C1 scan/query + pagination | 9 | provider against facade stub |
| 11 | C2 filter translation | 10 | round-trip filter tests |
| 12 | Architecture guards + docs + distilled route | all | `eng/verify.sh` |

Packages 1–4 are the first vertical slice and are independently
deliverable. Packages 5–6 are the dominant cost. Package 8 is deliberately
last.

## 9. Exit criteria / definition of done

For each package, per the repo's definition of done: typed contracts, the
success/failure/cancellation paths tested, no prohibited dependency,
actionable diagnostics, docs/ADRs updated, clean `eng/verify.sh`.

Track S is done when:

- An unmodified GeoServices client can discover the catalog, read a
  Geometry Service operation and run a FeatureService `query` against the
  demo store, with results matching the spec's response shapes.
- `simplify` (repair) and `generalize` (Douglas-Peucker) are distinct and
  both correct; the semantic trap has an explicit regression test.
- Unsafe `where` and the `{"url": ...}` form are rejected with typed
  errors; no request text reaches SQL as structure.

Track C is done when:

- A configured ArcGIS REST service appears in `GET /api/catalogue` and
  round-trips features through `QueryAsync` into canonical batches.
- Pagination, cancellation and remote-error mapping are tested; the
  provider is exercised against the Track S facade.

## 10. Risks

| Risk | Mitigation |
| --- | --- |
| Name-trap (`simplify` vs `generalize`) silently wrong | Two distinct verbs, explicit regression fixture (S1b) |
| Scope creep into Map/Image/GP | Non-goals are fixed in ADR-0035; facade advertises read-only capabilities |
| `where` translation diverges from the engine's grammar | Single shared parser/fixture set; reject anything not provably supported |
| WKID map drift / EPSG mismatch | Curated map with tests; unknown codes rejected, never guessed |
| New verbs bloat one interface | Granularity settled in package 5 with an ADR; adapter has no algorithms either way |
| Editing forces a store-contract change | Package 8 is explicitly gated on its own ADR |
| v1.0 target misses client expectations | §3 delta recorded; shape leaves room for 10.x parameters |

## 11. Traceability and open questions

Implements ADR-0035, under ADR-0033; respects ADR-0001, 0005, 0009, 0020.
Reviewed gaps: `architecture/references/geoservices-compatibility.md`.

**Open questions** to settle before/within the owning package:

1. Layer-id assignment for a FeatureServer (deterministic derived vs
   explicit config) — S2.
2. Operation-interface granularity for the new verbs — S1b/package 5.
3. Whether `spatialRel` beyond `esriSpatialRelEnvelopeIntersects` is
   required for the first read-only release — S2.
4. Whether the facade should also expose a minimal `MapServer` metadata
   resource (no tiles) so clients that insist on one can discover layers —
   deferred, likely not needed for FeatureServer-only use.
