---
status: accepted
date: 2026-09-17
deciders: maintainer + agent
---

# ADR-0053: A Map is the unit of authoring and exposure; services are projections

## Context

ADR-0041 made `Publication` the engine's neutral unit of exposure: a named,
ordered projection of datasets from one keyed store onto **one** protocol
surface, selected by `PublicationKind` (`Feature`, `Map`, `Image`). ADR-0047
added a per-layer MapLibre style fragment. ADR-0048 and ADR-0051 then
projected the `Map` and `Image` kinds onto the GeoServices MapServer and
ImageServer.

Using the workbench composer has exposed three limits:

1. **One publication is one protocol.** Exposing the same data as a
   FeatureServer, a MapServer, WMS, WFS and a tile service means authoring
   and maintaining several publications whose layer lists and styles must be
   kept in sync by hand.
2. **The authoring entity is the exposure decision.** A user composes a
   *map* — a named, ordered, styled set of layers — and only then decides
   which protocols expose it. Today the workbench asks for the protocol
   while composing, and the persisted record is named after the internal
   projection, not the thing the user made.
3. **Imagery cannot live on the same map.** Raster layers surface only as a
   separate `PublicationKind.Image` publication, so a map cannot present
   vector and imagery layers together and expose them as one ImageServer.

The engine already has every projection machinery in place (ADR-0044 render,
ADR-0046 tiles, ADR-0048 MapServer, ADR-0051 rasters); what is missing is a
single authoring aggregate that owns the layers and declares the exposure
set. Two decisions from ADR-0041/0047 are retained verbatim: layer ids are
stable, append-only and never renumbered, and the per-layer style is the
MapLibre fragment.

## Decision

### 1. `Map` replaces `Publication` as the neutral, persisted unit

`Spatial.PluginSdk` carries a core-typed aggregate and an `IMapRegistry`
capability. `Publication`, `PublicationKind`, `PublicationLayer` and
`IPublicationRegistry` are removed; their behaviour moves to the map.

```csharp
public enum MapService { Feature, Map, Wms, Wfs, Tiles, Image }

public enum MapLayerKind { Feature, Image }

public sealed record MapLayer(
    string Dataset,
    int LayerId,
    string? Name = null,
    string? Style = null,
    MapLayerKind Kind = MapLayerKind.Feature,
    string? Store = null);

public sealed record Map(
    string Name,
    string Store,
    IReadOnlyList<MapLayer> Layers,
    IReadOnlyList<MapService> Services,
    string? Description = null,
    string? Copyright = null);
```

- `LayerId` keeps the ADR-0041 rules: assigned once, persisted, append-only,
  never reused or renumbered.
- `Style` is the ADR-0047 MapLibre fragment. Because a layer is owned by a
  map, **the same dataset can be styled differently in different maps**;
  styles are never global and never shared.
- Visibility is not a separate field: it is the `layout.visibility` already
  carried by the ADR-0047 style fragment, so there is one source of truth.
- `Kind` separates vector datasets from raster datasets. `Store` optionally
  overrides the map's store per layer, so one map can mix a feature store
  (`memory`, `postgis`, `demo`) with the keyed `raster` store and surface the
  raster layers as an ImageServer (ADR-0051).
- `Services` is the exposure set. Any subset of `Feature`, `Map`, `Wms`,
  `Wfs`, `Tiles`, `Image` is valid. An empty set is a valid draft that serves
  nothing. This is what makes one map expose several endpoints.

The `MapLayer.Kind` selects which services a layer feeds: `Feature` layers
feed Feature/Map/Wms/Wfs/Tiles; `Image` layers feed Image. A map that enables
a service with no matching layer is a validation error at `PutAsync`.

### 2. `IMapRegistry` replaces `IPublicationRegistry`

The interface shape and persistence model are unchanged (ADR-0041 §2):
declared, config-seeded maps are immutable through the API; runtime maps live
in a versioned JSON document written atomically under a single-writer lock.
Only the stored aggregate and the file name change.

- Runtime file: `Spatial:Maps:Path` (default `./data/maps.json`).
- Declared maps: `Spatial:Maps:Declared`; the legacy
  `Spatial:GeoServices:Services` entries project to Feature-only maps.
- A legacy `Spatial:Publications:Path` file, when present, is read once and
  migrated: each `Publication` becomes a `Map` whose `Services` is the single
  service matching its old `Kind`. Migration is read-only and never rewrites
  the legacy file.

Validation follows ADR-0041: flat identifier names, strict `schema.table`
datasets, unique non-negative layer ids, a non-empty layer list, JSON-array
styles, and every enabled service must be fed by at least one layer of the
matching kind. New failures are `invalid.arguments`. The host also resolves
each layer against its store's catalogue before storing the map — a feature
layer must describe and an image layer needs the store's `IRasterCatalogue` —
so a dangling layer or a store with no raster provider can never be published
as a service that fails on every request.

### 3. Each service is an independent projection of one map

| `MapService` | Route | Layers | Machinery |
| --- | --- | --- | --- |
| `Feature` | `{root}/{name}/FeatureServer…` | feature | ADR-0035/0037/0038 |
| `Map` | `{root}/{name}/MapServer…` | feature | ADR-0048 |
| `Tiles` | `/api/maps/{name}/tiles/{z}/{x}/{y}.{fmt}` | feature | ADR-0046 + ADR-0044 |
| `Wms` | `/ogc/{name}/wms` | feature | ADR-0044 + query verbs |
| `Wfs` | `/ogc/{name}/wfs` | feature | ADR-0035 query engine |
| `Image` | `{root}/{name}/ImageServer…` | image | ADR-0051 |

The GeoServices adapter resolves a **map** by name and requires the requested
server's `MapService` to be enabled; a name that exists but does not expose
that service is `not.found` (404), exactly as an unknown name is today. The
`ResolvedService` shape (store, explicit layers, description, copyright) is
retained so the existing MapServer/FeatureServer/ImageServer code is
unchanged apart from the resolver.

WMS and WFS are new OGC boundary adapters. They are read-only projections of
feature layers:

- **WMS 1.3.0**: `GetCapabilities` (XML), `GetMap` (renders the map's style
  through `IMapRenderer` over the requested EPSG bbox/size), and
  `GetFeatureInfo` (queries the identified layers through the feature query
  verbs and returns the requested info format).
- **WFS 2.0.0**: `GetCapabilities` (XML), `DescribeFeatureType` (XSD), and
  `GetFeature` (GeoJSON; GML is a recorded non-goal until measured demand).

Both live in one implementation project, `Spatial.Adapter.Ogc`, which may
reference `Spatial.Core`, `Spatial.PluginSdk` and `Spatial.Interop.Esri`
(the shared codec), never the GeoServices adapter. OGC XML is generated in
that adapter and never enters a contract. The OGC routes are the only new
protocol surface; no core type changes for them.

The neutral tile route renders the map's composed style and layers through
the existing `TileService`/`ITileScheme`/`ITileCache` contracts, so a map's
`Tiles` service needs no new rendering path.

### 4. Host API and clients

- `GET /api/maps`, `GET /api/maps/{name}`, `PUT /api/maps/{name}`,
  `DELETE /api/maps/{name}` replace the publication routes. `PUT`/`DELETE`
  stay admin-token gated (ADR-0041 §6).
- `POST /api/maps/{name}/render` replaces the publication render route.
- `POST /api/ingest`'s `publish` parameter registers the uploaded dataset on
  a Feature map, as today.
- `/api/publications` and `/api/publications/{name}/render` remain as
  deprecated aliases for one release, so `tools/seed` and deployed clients
  keep working while they move; the aliases are removed in the next release.
- The Esri admin projection maps its create/delete/publish operations onto
  maps (`kind` accepts `FeatureServer` for compatibility).
- The TypeScript and .NET SDKs expose map methods; the OpenAPI snapshot and
  generated TypeScript types are regenerated together.

### 5. Workbench

The composer becomes the **Maps** experience: a named map with ordered
layers, per-layer style, a service toggle group (Feature, Map, Tiles, WMS,
WFS, Image), a preview, and copyable endpoint URLs for every enabled
service. Consuming a map is therefore possible without leaving the
workbench. The Data screen continues to ingest and publish; its "publish"
target becomes a map.

## Consequences

- There is one authoring entity and one persisted document; there is no
  `Publication`/`Map` pair to keep in sync.
- The same dataset can be reused across maps and styled independently in
  each, because style lives on the map's layer.
- Existing GeoServices serving, editing, rendering, tiles and imagery are
  reused, not rewritten; the adapter change is a resolver change plus a
  capability check.
- WMS/WFS add two protocol surfaces and one implementation project with its
  own tests; they add no SDK surface beyond the map they read.
- The contract, SDKs, tests, seed tool, workbench, OpenAPI snapshot and this
  ADR land together (AGENTS.md). The distilled `contracts.md` and
  `host-and-clients.md` are updated in the same change.
- Architecture tests gain `Spatial.Provider.Maps` (replacing
  `Spatial.Provider.Publications`) and `Spatial.Adapter.Ogc` on the platform,
  implementation and host allowlists.
- A pre-1.0 persisted file migrates on read; no data is lost and the legacy
  file is left intact.

## Alternatives

- **Keep `Publication` and add a `Map` wrapper.** Two persisted aggregates
  with duplicated draw order and styles; drift risk, and the wrapper would
  still need a multi-service field. Rejected.
- **Keep `PublicationKind` and enumerate one publication per service.** The
  registry is keyed by name, so several kinds per name collide; suffixing
  names leaks the exposure decision into the identifier. Rejected.
- **Model services as separate `MapService` records referencing a map.**
  Adds a second persisted list whose only content is a boolean per protocol.
  Rejected; the set on the map is the same information with one source.
- **Implement WMS/WFS inside the GeoServices adapter.** It would import OGC
  vocabulary and XML into the Esri boundary and blur the adapter's one job.
  Rejected.

## Implementation status

Foundation, GeoServices projection, the neutral map tile route, the host
API, SDKs, workbench Maps experience, seed tooling and docs are implemented.
WMS 1.3.0 and WFS 2.0.0 are implemented in `Spatial.Adapter.Ogc`:
`GetCapabilities`/`GetMap`/`GetFeatureInfo` over the render pipeline and
`GetCapabilities`/`DescribeFeatureType`/`GetFeature` (GeoJSON) over the query
verbs, both mounted under `Spatial:Ogc:Root` at `/{name}/wms` and
`/{name}/wfs`. GML output is a recorded non-goal and is a typed
`invalid.arguments` ServiceException. Raster map layers expose ImageServer
through the `raster` store.

## References

- ADR-0041 (publications are protocol-neutral), ADR-0047 (persisted layer
  style), ADR-0048 (MapServer projection), ADR-0051 (raster boundary),
  ADR-0044 (raster rendering pipeline), ADR-0046 (tiles)
- `architecture/distilled/contracts.md`, `host-and-clients.md`
