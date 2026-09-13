# Map Service (MapServer) Implementation Plan — scaffold

> **Status:** M0 (data-only MapServer) implemented under ADR-0047 together
> with per-layer render style; M1–M4 remain scaffold. Reuses the publication
> registry and admin surface from `publishing-and-ingest-plan.md`. Read
> `architecture/references/geoservices-compatibility.md` §4 first.
>
> **Blocking decision:** MapServer is only useful to ArcGIS clients if it
> can render (`export`/tiles). The engine is headless by design
> (principles 1–2), so the renderer is the whole plan. M0 is useful and
> cheap without one; M2 is the expensive commitment.

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
| `drawingInfo`, renderers, symbols, labels, domains (§12–15) | style model (absent) |

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

### M0 — Data-only MapServer — **delivered (ADR-0047)**
- **Delivered:** `/{service}/MapServer` root for a `PublicationKind.Map`
  publication: layers, tables, `spatialReference`, extents (computed by
  scanning the layers — the stores expose no extent capability yet),
  `units`, `copyrightText`; `layers` (All Layers and Tables); Layer/Table
  with `drawingInfo` lowered from the layer's optional `LayerStyle`
  (ADR-0047); and `query` reusing the FeatureServer query engine. The root
  advertises `capabilities: "Query,Data"` and never `Map`, because export is
  not served.
- **Proof:** `EsriMapModelTests` pin the style → symbol lowering, colour
  parsing and units; `AdminEndpointTests.A_map_publication_is_served_as_a_styled_map_server`
  drives a real styled publication from ingest to catalog, root, `layers`,
  layer `drawingInfo` and query; `Spatial.Provider.Memory` reports the real
  inferred geometry type so the symbol family is correct.

### M1 — Identify and Find
- **Deliverable:** `identify` (point/envelope + tolerance + layer ids;
  returns layer/feature/attributes/geometry) and `find` (search fields,
  `contains`/`startsWith` search text over layers) built on the safe
  `where` filter subset and geometry distance/relation verbs.
- **Proof:** spec examples + edge cases (empty result, multiple layers,
  unicode search text, missing tolerance).

### M2 — Export Map (renderer)
- **Deliverable (only after the render ADR):** `export` returning
  `{href,width,height,extent,scale, ...}` plus `f=image` streaming;
  `transparent`, `dpi`, `format`, `imageSR`/`mapSR`, `layers=show:…`,
  `layerDefs`.
- **Renderer:** a minimal style model (simple renderer: single symbol per
  layer, fill/stroke/opacity) over core geometry → raster; no labels or
  class breaks in this phase.
- **Proof:** golden-image tests at fixed extent/dpi (tolerance-compared),
  projection correctness, transparent background.

### M3 — Tiles
- **Deliverable:** `tile/{z}/{y}/{x}` and `tileInfo`/LODs for a
  `singleFusedMapCache` publication; cache keyed by service+LOD+tile with a
  configurable directory and eviction; dynamic rendering fallback for
  uncached tiles.
- **Proof:** tile determinism, LOD math against the spec's tiling scheme,
  cache hit/miss behaviour, cancellation.

### M4 — Style metadata (§12–15)
- **Deliverable:** `drawingInfo`/renderer/symbol/label/domain JSON for the
  subset the renderer honours; unrecognised renderer types are rejected
  rather than silently flattened.
- **Proof:** round-trip of the spec's example renderers; architecture test
  that renderer types never enter `Spatial.PluginSdk`.

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
