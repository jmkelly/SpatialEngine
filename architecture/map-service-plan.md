# Map Service (MapServer) Implementation Plan — scaffold

> **Status:** implemented (M0–M4, ADR-0048). The blocker — no renderer —
> was cleared by ADR-0044 (raster pipeline), ADR-0046 (tiles) and ADR-0047
> (persisted layer style), so the MapServer now serves metadata, query,
> identify, find, export and tiles over a `PublicationKind.Map` publication.
> Reuses the publication registry and admin surface from
> `publishing-and-ingest-plan.md`. Read
> `architecture/references/geoservices-compatibility.md` §4 first.
>
> **Blocking decision (resolved):** MapServer is only useful to ArcGIS
> clients if it can render (`export`/tiles); the renderer now exists and is
> reached through the SDK contracts. The remaining non-goal is richer style
> metadata (class breaks, unique value, labels) — see ADR-0048.

## 1. What the spec requires (v1.0 §4)

| Resource / operation | Depends on |
| --- | --- |
| Map Service root (§4.0): layers, tables, `spatialReference`, `singleFusedMapCache`, `tileInfo`, `initialExtent`, `fullExtent`, `mapName`, `units`, `copyrightText`, `timeInfo` | publication metadata |
| Export Map (§4.0.4) | **renderer** + image encoding |
| Identify (§4.0.5) | feature query + geometry ops (present) |
| Find (§4.0.6) | attribute query (present) |
| Map Tile (§4.1) | **tile cache or renderer** |
| Layer/Table (§4.2) + Query (§4.2.4) | same as FeatureServer layer/query |
| Query Related Records (§4.2.5) | relationship model (absent, ADR-0035 non-goal) |
| Feature (§4.3), Attachment Infos/Attachment (§4.4–4.5), HTML Popup (§4.6), Image (§4.7) | attachments/relationships (absent) |
| All Layers and Tables (§4.8) | publication metadata |
| `drawingInfo`, renderers, symbols, labels, domains (§12–15) | style model (per-layer style now persisted — ADR-0047; the renderer/`drawingInfo` projection is still absent) |

Map services are read-only (spec §4.0) — no editing concerns.

## 2. Context and precedent

- The workbench already **consumes** Esri basemap tiles
  (`.../MapServer/tile/{z}/{y}/{x}`), so the wire shape is known; nothing
  in the engine produces them.
- `PublicationKind.Map` in `publishing-and-ingest-plan.md` is the natural
  carrier: a MapServer is a named, ordered set of layers from one store
  plus optional style metadata.
- Identify/Find/Query/Layer/All-Layers reuse the existing `IDataCatalogue`
  / `IFeatureStore` / `IGeometryRelations` / `ICoordinateTransforms` paths
  and the GeoServices query/`where` subset already proven against ArcGIS
  REST JS.

## 3. Decision: rendering strategy

| Option | Shape | Cost / risk |
| --- | --- | --- |
| **A. Data-only MapServer** | serve metadata, layer/query, identify, find, all-layers; reject export/tiles with a typed `invalid.arguments` | cheap, reuses FeatureServer; **not accepted by ArcGIS MapView** (needs tiles/export) |
| **B. In-process rasterizer** | a `Spatial.Render.*` implementation (SkiaSharp or similar) + a minimal `drawingInfo`/symbol subset, PNG/JPEG output | one ADR + package allowlist; no native service; style/model surface is large |
| **C. External renderer** | an implementation project shells out to GDAL/MapServer | native dependency, process management, container bloat; conflicts with the in-process default |
| **D. Vector tiles (MVT)** | generate MVT from datasets; modern clients render | not MapServer-compatible; a different protocol entirely (OGC/Mapbox), possibly worth more than MapServer |
| **E. Basemap reverse-proxy** | MapServer `tile`/`export` proxied to a remote service | no local data rendering; SSRF/token/config concerns; only for basemaps |

**Decided:** ship **M0 (data-only)** because it makes the server
discoverable and queryable from ArcGIS clients and reuses everything
already built. Defer **M2 (rendering)** until a real client needs it, then
prefer **B** (in-process, JIT, no sidecar). Record the choice in its own
ADR: `architecture/decisions/ADR-0044-raster-rendering-pipeline.md` with the
execution plan at `architecture/rendering-implementation-plan.md`
(ADR-0041 covers ingest/publications; ADR-0042/0043 are reserved by
`publishing-and-ingest-plan.md`).
Option D is a separate
future track and should not be smuggled in as "MapServer".

## 4. Phases

### M0 — Data-only MapServer
- **Implemented:** `/{service}/MapServer` root (layers/tables,
  `spatialReference`, extents, `units`, `copyrightText`,
  `capabilities: "Map,Query,Data"`, `singleFusedMapCache`, `tileInfo`),
  `.../layers` (all layers and tables), `.../{layerId}` and
  `.../{layerId}/query` (the FeatureServer query engine). Map publications
  are advertised as `MapServer` in the catalog.
- **Proof (built):** HTTP tests over a runtime Map publication
  (`GeoServicesMapTests`); capabilities match what is served. The ArcGIS
  REST JS e2e (`clients/typescript/test/geoservices-e2e.test.ts`, run by
  `eng/e2e-web.sh` against a declared Map publication) drives discovery,
  identify, export and tiles with the official Esri client over a temporary
  host port.

### M1 — Identify and Find
- **Implemented:** `identify` (point/envelope + pixel tolerance + layer
  selection → layer/feature/attributes/geometry) and `find` (search fields,
  `contains`/`startsWith`) built on the store scan and geometry verbs.
- **Proof (built):** HTTP tests (a city identified under its point; a text
  match returns the matched field and value); unit tests for the layer
  selection parameter.

### M2 — Export Map (renderer)
- **Implemented:** `export` streams `f=image` bytes (png/jpg/webp/tiff) and
  returns `{href,width,height,extent,scale}` for `f=json`; `transparent`,
  `dpi`, `bboxSR`/`imageSR` (with bbox reprojection), `layers=show|hide` and
  `layerDefs` (the safe filter grammar pushed down to the store, never SQL).
- **Proof (built):** HTTP tests render a PNG and a JSON href at a fixed
  extent/size; distinct persisted styles are tested at the publication route.
- **Renderer:** the persisted per-layer style (ADR-0047) composed by
  `PublicationMapStyle`, drawn by `IMapRenderer`; no labels or class breaks.

### M3 — Tiles
- **Implemented:** `tile/{z}/{y}/{x}` rendered through the registered
  Web-Mercator `ITileScheme` and content-addressed `ITileCache` (ADR-0046),
  with `tileInfo`/LODs and `singleFusedMapCache: true` on the root.
- **Proof (built):** an HTTP test renders tile 0/0/0; `tileInfo` LODs are
  asserted on the root. Cache ownership is the ADR-0046 in-memory cache.

### M4 — Style metadata (§12–15)
- **Implemented:** `drawingInfo` with a `simple` renderer, projected from
  the persisted MapLibre fragment (fill → `esriSFS`, line → `esriSLS`,
  circle → `esriSMS`); richer renderer types are not produced (ADR-0048).
- **Proof (built):** unit tests round-trip the projection and colour
  parsing; an HTTP test reads a layer's `drawingInfo`. The renderer types
  stay in the adapter — no renderer type enters `Spatial.PluginSdk`.

## 5. Non-goals (standing)

Query Related Records, attachments, HTML Popup (no relationship/attachment
model — ADR-0035), time-aware maps, `gdbVersion`/versioning, MapServer
editing (spec forbids it), print/geoprocessing.

## 6. Risks

- The renderer is a large, unbounded surface (symbols, labels, scale
  dependencies). If M2 is attempted, the style model must be explicitly
  small and its non-goals tested.
- Golden-image tests are brittle across platforms; pin fonts/anti-aliasing
  or compare with a tolerance budget.
- `singleFusedMapCache` semantics imply a cache lifecycle the engine has no
  home for; decide cache ownership (host filesystem vs PostGIS) before M3.
- Advertising `Map` capability without `export` will confuse clients;
  capability strings must match what is actually served.

## 7. References

- `architecture/publishing-and-ingest-plan.md` (Publication/registry/admin)
- `architecture/geoservices-implementation-plan.md` (query/edit engines)
- `architecture/references/geoservices-compatibility.md` §4
- ADR-0035, ADR-0037, ADR-0040 (metrics), principles 1–2
