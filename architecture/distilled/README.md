# Distilled Architecture

The condensed architecture documentation for this repo. Kept deliberately
small for fast LLM/human consumption. **Precedence when documents disagree:**
`architecture/decisions/` (ADRs) beat these digests; fix a digest instead of
diverging from an ADR. The ADRs are dated decision records — their prose
reflects the state at decision time.

## Route by task

| Task | Read | Related ADRs |
| --- | --- | --- |
| Core geometry / feature types, codecs | `core.md` | 0001, 0004, 0009, 0020, 0029, 0032 |
| Service interfaces, implementations, composition | `runtime.md` | 0033 |
| Implementation projects and DI lifecycle | `plugins.md` | 0033 |
| Which services exist + their contracts | `contracts.md` | 0033 |
| HTTP API, config, SDKs, frontend, deployment, secrets | `host-and-clients.md` | 0014–0019, 0033 |
| Esri GeoServices REST (serve/consume) | `host-and-clients.md`, `../references/geoservices-compatibility.md` | 0035, 0037, 0048 |
| Ingest, runtime service publishing, Esri admin | `contracts.md`, `host-and-clients.md` | 0041, 0037, 0038 |
| Map composer (layers, styling, drag/drop, upload) | `host-and-clients.md` | 0014, 0041, 0047 |
| MapServer / ImageServer | `host-and-clients.md`, `../image-service-plan.md` | 0035, 0048, 0050, 0051 |
| Command-line workspace (datasets, maps, project file) | `cli.md` | 0041, 0047, 0052 |
| Raster rendering / imagery / tiles / labels | `rendering.md`, `../../research/rendering/README.md` | 0044, 0046, 0049 |
| Any architectural change | this file + `../principles.md` | — |

## The twenty principles (see ../principles.md)

Superseded by ADR-0033 where they assumed worker plugins; the standing
shape is noted in brackets.

1. Geometry is core; spatial algorithms are not.
2. Headless engine — every UI is a client.
3. The browser workbench is the frontend.
4. Packaging is not architecture; the host ships standalone.
5. The .NET host runs independently of every client.
6. Contracts outlive implementations. [Interfaces in `Spatial.PluginSdk`.]
7. Implementations depend on contracts, never on each other.
8. No implementation-specific geometry object crosses a service boundary.
9. Core geometry values are immutable.
10. Data stores are providers, not the domain model.
11. Long-running operations are cancellable `Task`s. [No job model.]
12. Persistent state is external. [Implementations are linked, not disposable workers.]
13. Open formats at boundaries. [In-process DI; canonical binary on HTTP.]
14. Agents and human clients use the same public services.
15. Provider pushdown is optional and must preserve contract semantics.
16. Every derived result records its service. [No provenance envelopes.]
17. Kernel stays small, stable, independently testable.
18. Another language only where profiling or platform integration justifies it.
19. No Native AOT until compatibility is demonstrated.
20. The host and every client share the same conformance tests.

## ADR register (one line each; full records in `../decisions/`)

| ADR | Decision in one line |
| --- | --- |
| 0001 | Geometry values are core; algorithms are never core. |
| 0002 | ~~Spatial operations are capability plugins~~ — superseded by 0033. |
| 0003 | ~~Data stores are provider plugins~~ — superseded by 0033. |
| 0004 | Core geometry values are immutable. |
| 0005 | Third-party types (NTS, Npgsql, EF, renderer) never cross public contracts. |
| 0006 | ~~Isolated worker processes~~ — superseded by 0033 (in-process default). |
| 0007 | ~~Versioned capability contracts~~ — superseded by 0033 (typed interfaces). |
| 0008 | ~~Jobs for long operations~~ — superseded by 0033 (cancellable Tasks). |
| 0009 | CRS identity is core; transformation is a service. |
| 0010 | PostGIS is the initial backing provider. |
| 0011 | .NET 10 backend/runtime; boundaries stay core-typed. |
| 0012 | Host is JIT-compiled initially. |
| 0013 | ~~Language-neutral worker boundaries~~ — superseded by 0033. |
| 0014 | React + TypeScript + MapLibre frontend; talks only to the public host API. |
| 0015 | ~~Browser milestone completes before any Tauri work~~ — superseded by 0039. |
| 0016 | ~~Tauri 2 is the desktop shell~~ — superseded by 0039. |
| 0017 | ~~Tauri contains no spatial logic~~ — superseded by 0039. |
| 0018 | `Spatial.Host` is independently executable; API identical for every client. |
| 0019 | ~~Tauri may bundle Spatial.Host as an optional sidecar~~ — superseded by 0039. |
| 0020 | Canonical binary interchange is the required wire format; JSON only for debugging/metadata. |
| 0021 | Native AOT only with measured benefit + compatibility evidence; main host stays JIT. |
| 0022 | ~~Runtime-owned resource handles~~ — superseded by 0033 (store-owned handles). |
| 0023 | ~~Bounded backpressured streams~~ — superseded by 0033 (batch pages). |
| 0024 | ~~Observable job state machines~~ — superseded by 0033. |
| 0025 | ~~Line-delimited JSON worker wire~~ — superseded by 0033. |
| 0026 | ~~Versioned geometry operation contracts~~ — superseded by 0033 (`IGeometryOperations`). |
| 0027 | ~~Transformation contracts~~ — superseded by 0033 (`ICrsDirectory`/`ICoordinateTransforms`). |
| 0028 | ~~PostGIS provider contracts~~ — superseded by 0033 (`IDataCatalogue`/`IFeatureStore`/`ITransactionStore`). |
| 0029 | Feature model gains contract faces (`IFeature`…); `FeatureBatchCodec` → `Core.Features.Codec`. |
| 0030 | ~~Host API capability envelopes~~ — superseded by 0033 (typed routes). |
| 0031 | ~~Workbench hosting + plugin control~~ — superseded by 0033 (static serving stays, control endpoints removed). |
| 0032 | Geometry contract faces (`IPoint`…`IGeometryFactory`); `GeometryCodec` → `Core.Geometry.Codec`. |
| 0033 | In-process service interfaces + DI replace worker plugins. |
| 0034 | Aspire AppHost composes the local development profile. |
| 0035 | GeoServices REST is an adapter-owned boundary; ArcGIS REST is consumed as a provider. |
| 0036 | Geometry measurement, processing and relation verbs are separate SDK interfaces. |
| 0037 | Feature editing is a gated, per-feature `IFeatureEditStore` capability. |
| 0038 | Read-by-identity is an additive store capability (`IFeatureLookup`). |
| 0039 | Desktop (Tauri) packaging is abandoned; host + browser workbench are the product. |
| 0040 | Metrics gate thresholds are evidence-based; raw LCOM4 gates through guarded diagnoses. |
| 0041 | Ingest and publications are protocol-neutral SDK capabilities; Esri admin is a gated projection. |
| 0044 | Raster rendering is a pipeline over Skia (vector) + NetVips (imagery); contracts in SDK, implementations by DI. |
| 0045 | Structured logging is Serilog to Seq; Aspire runs the Seq server in development. |
| 0046 | Tiling schemes and the tile cache are pluggable SDK contracts; Web-Mercator + an in-memory LRU cache are the first implementations. |
| 0047 | Layer style is persisted on the publication as a per-layer MapLibre fragment (ADR-0044 dialect). |
| 0048 | A MapServer is a projection of a map over the SDK render and tile contracts. |
| 0049 | Labels/symbols shape with HarfBuzz over an embedded pinned font and draw embedded SVG sprites; deterministic collision; new Skia.HarfBuzz/Svg.Skia packages are allowlisted. |
| 0050 | Rich MapServer renderers, labels and domains are an adapter projection of the persisted MapLibre style; §4.7 images are a typed `not.found`. |
| 0051 | Rasters are provider-owned; contracts carry only encoded images and core-typed metadata/geometry, never raster values or third-party types. NetVips is the engine; GDAL needs measured demand. |
| 0052 | The Spatial CLI is a dependency-free public-API client; a versioned declarative project file captures datasets + maps and lowers to publications. |
| 0053 | A Map is the unit of authoring and exposure; its Feature/Map/Tiles/WMS/WFS/Image services are projections of one map. |
| 0054 | ImageServer missing resources: legend, find, statistics/histograms, attribute table, thumbnail/metadata. |
| 0055 | MapServer legend, queryDomains/queryLegends and per-layer generateRenderer are adapter projections of the persisted MapLibre style. |
| 0056 | Feature modern query params (`returnEnvelope`, `resultPaginationToken`, `defaultSR`, `uniqueIds`) are an adapter projection; no SDK/Core change. |
| 0057 | ImageServer capability flags (raster-function/mosaic/mensuration/download honesty) plus reject-by-name for mensuration/multidimensional/catalog-write ops. |
| 0057 | Feature percentile statistics and capability-flag honesty (COUNT DISTINCT, percentile type, five advertised flags). |
| 0058 | MapServer export reads time/timeRelation/layerTimeOptions, dynamicLayers and layerOption; cached-root fields follow the served scheme. |
| 0059 | Map/Image offline and async surface (exportTiles, WMTS, KML, jobs) is rejected by name; T-048 builds on that scope. |
| 0060 | Feature write-model extensions: service-level query, `generateRenderer` reuse, `validateSQL`, aggregation honesty, attachment store-model decision. |
=======
>>>>>>> 7f9676f (Serve Feature modern query params (returnEnvelope, defaultSR, pagination token, uniqueIds))

## How to change the architecture

1. Decision change → new/updated ADR in `architecture/decisions/` **first**.
2. New service → interface + implementation + tests, together.
3. New package in a platform project → ADR required before the architecture-test allowlist accepts it.
4. Enforcement lives in `tests/architecture/Spatial.Architecture.Tests`; every rule names the principle/ADR it implements.
5. Public changes update contracts, SDKs, tests, ADRs **and the relevant distilled doc** together.

## Documentation health notes

- ADRs 0002/0003/0006–0008/0013/0022–0028/0030/0031 describe the retired
  worker-plugin model; they are kept as history and marked superseded
  above. Read them for rationale, not for current structure.
- ADRs 0015–0017/0019 describe the abandoned Tauri desktop shell (ADR-0039);
  kept as history, not for current structure.
- Delivery plans are removed once their track is implemented (the GeoServices,
  publishing/ingest, MapServer, composer and rendering plans are retired);
  older ADRs may still name them as dated records. `image-service-plan.md`
  stays while I5 (cache and limits) is open.
- Byte-level wire specs: `GeometryCodec`/`FeatureBatchCodec` source is the
  authoritative format spec; `core.md` carries the essentials.
