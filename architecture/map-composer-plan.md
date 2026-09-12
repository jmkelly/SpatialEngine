# Map Composer Implementation Plan

> **Status:** MVP implemented. This is P7 ("Clients & workbench") of
> `architecture/publishing-and-ingest-plan.md` grown into a first-class
> authoring surface. It adds **no host contract**: it is a browser client of
> the existing `POST /api/ingest` and `PUT /api/publications/{name}` routes
> (ADR-0041), rendered with MapLibre (ADR-0014). Read
> `publishing-and-ingest-plan.md` and
> `architecture/distilled/host-and-clients.md` first.

## 1. The problem

The workbench can already **upload** a file (`Data` screen) and **inspect**
one dataset (`Map` screen), but it cannot **compose**: a user with several
engine datasets cannot order them, style them, see the composition on a map,
and publish it as one named service. Publishing today means hand-writing a
`Publication` JSON body, and there is no preview of what the service will
contain.

## 2. Goal and non-goals

**Goal.** A `Composer` screen that turns a set of engine datasets into one
named publication:

```text
  catalogue (store) ──► add layer ──► style (MapLibre) ──► reorder (drag/drop)
        │                                                        │
   upload a file ──► ingest (memory/postgis) ──────────────────► │
                                                                 ▼
                                              PUT /api/publications/{name}
                                              (kind = feature | map)
```

The screen is a **client of the public host API only** (through the TS SDK)
and holds **no spatial logic** — it uses the existing `sgeom.ts` decoder to
preview canonical geometry, exactly like the `Map` screen.

**Non-goals (MVP):**

- **No style persistence.** Per-layer style is an authoring aid held in
  browser state; the published `Publication` carries ordered layers only.
  Persisting style needs the MapServer render model (M2/M4 of
  `map-service-plan.md`, ADR-0044) and its own ADR; until then a MapServer
  is data-only (M0) and cannot honour style anyway.
- **No server-side rendering.** The MapLibre preview is the map; the host
  still serves no tiles/export.
- **No multi-store publication.** A publication names one store, so the
  composer is single-store; switching store resets the layer list.
- **No new format decoders.** GeoJSON, NDJSON and CSV via the existing
  ingest path; Shapefile/GPKG stay P8.
- **No contract, SDK or ADR changes.** The layer model maps onto the
  existing `Publication`/`PublicationLayer` records; stable `LayerId`s are
  preserved when an existing publication is loaded and `-1` (assign) is
  sent for new layers.

## 3. Where it sits (boundaries)

| Concern | Project | Rule |
| --- | --- | --- |
| Composer model + MapLibre style specs | `apps/workbench-web/src/composer.ts` | pure, no JSX, unit-testable |
| Basemap selection | `apps/workbench-web/src/basemap.ts` | shared with the `Map` screen |
| Map fit math | `apps/workbench-web/src/map-geometry.ts` | pure projection math |
| Screen wiring (map + panel) | `apps/workbench-web/src/screens/ComposerScreen.tsx` | SDK + DOM only |

## 4. Design

### 4.1 Model

```ts
type GeometryKind = "point" | "line" | "polygon" | "mixed";

interface LayerStyle { color; opacity; lineWidth; radius; visible }
interface ComposerLayer {
  id; store; dataset; name; geometry; style; featureCount;
  layerId: number | null;      // published id when loaded, null for new
}
interface ComposerDraft { name; kind: PublicationKind; store; layers[] }
```

Geometry kind is inferred from the loaded feature collection (the memory
provider reports a generic `"Geometry"` type), and drives the default style
and which MapLibre layer kinds are emitted.

### 4.2 Mapping to the contract

- `toPublication(draft)` maps layers in **list order** to
  `PublicationLayer[]`, sending `layerId: l.layerId ?? -1` (`-1` is the
  registry's "assign the next free id" sentinel) and `name: l.name`.
- Order is the layer list top-to-bottom; the MapLibre preview adds specs in
  reverse so the first list row paints on top.
- `fromPublication(p)` hydrates the form (name, kind, store, layers) and
  preserves each `layerId`, so re-publishing keeps stable ids and appended
  layers never renumber existing ones.
- The host validates names (`[A-Za-z_][A-Za-z0-9_]*`), datasets
  (`schema.table`) and non-empty layer lists; the screen surfaces the typed
  `invalid.arguments`/`not.found` failures unchanged.

### 4.3 Interactions

- **Add layer** — pick a catalogue dataset for the store; scan it (canonical
  SFBAT → GeoJSON via `sgeom.ts`), infer kind, append, fit the map.
- **Drag and drop** — native HTML5 DnD reorders rows; `▲`/`▼` buttons are
  the accessible equivalent (and the deterministic test path).
- **Style** — per-layer colour, opacity, line width and point radius, with
  visibility; applied to the live map through `layerSpecs()`.
- **Upload & import** — file + dataset/format/SRID → `client.ingest(...)` →
  refresh catalogue → add as a layer. Disabled for the read-only `demo`
  store.
- **Publish / load / delete** — the three publication routes behind the
  admin token field (never stored).

## 5. Proof

- **Unit** (`apps/workbench-web/test/composer.test.ts`,
  `map-geometry.test.ts`): kind inference, default styles, add/remove/
  reorder/update, `toPublication`/`fromPublication` round-trip with stable
  ids, and the emitted MapLibre spec shapes (point→circle, line→line,
  polygon→fill+line, visibility).
- **Browser e2e** (`tests/end-to-end-web/tests/workbench.spec.ts`): add a
  demo layer, reorder it, style it, upload a GeoJSON into `memory`, add it,
  publish, and read the publication back from the list. The suite runs with
  an isolated publications file so runs never share state.
- **Gate**: `eng/workbench-e2e.sh` and `eng/e2e-web.sh` stay green;
  `eng/verify.sh` is unaffected (no .NET change).

## 6. Future (post-MVP)

- Style persistence + MapServer `drawingInfo` after the render ADR (M2/M4).
- Basemap/label layer options and per-layer opacity in the preview legend.
- Streaming/bounded previews for datasets larger than the in-memory page
  convention (P8), replacing the scan-the-whole-dataset preview.
