# Tiles compatibility matrix (cached tiles + WMTS + offline packages)

Sources (checked 2026-09-14):
- S1 Cached Map Service / `tile-map`: `.../enterprise/tile-map/`,
  `map-tile/` (`.../MapServer/tile/{z}/{y}/{x}`), `tileInfo`+LODs on the service root
- S2 `export-tiles-map-service/` + `estimate-export-tile-size-map-service/`
  (offline `.tpk`/`.vtpk` packages; `exportTilesAllowed`)
  and the Image variants `export-tiles-image-service/` +
  `estimate-export-tile-size-image-service/`
- S3 WMTS triple: `wmts-capabilities-map-service/` (`.../WMTS/1.0.0/WMTSCapabilities.xml`),
  `wmts-tile-map-service/`, `wmts-map-service/` (same triple for image services)
- S4 OGC WMTS 1.0.0 (07-057r7): https://portal.ogc.org/files/?artifact_id=35326 ;
  OGC API Tiles (S5, modern path): https://ogcapi.ogc.org/tiles/
- G1 Ground truth: `ground-truth/map-root-cached.WorldTopo.json`
  (`tileInfo`: 24 LODs, `storageInfo`, `exportTilesAllowed` to be compared)

Our surface: `src/Spatial.Host/Api/MapTileEndpoints.cs`
(`GET /api/maps/{name}/tiles/{z}/{x}/{y}.{format}`), `TileEndpoints.cs`
(`POST /api/render/tiles/...` + `capabilities`), MapServer
`tile/{z}/{y}/{x}` (`GeoServicesEndpoints.MapExport.cs`), `ITileScheme`/
`ITileCache` (ADR-0046), `Spatial.Tiling.WebMercator`.

## 1. Capabilities

| Capability | Ours | Status | Evidence |
|---|---|---|---|
| Live XYZ raster tiles (Web-Mercator, png/jpg/…) per map | served | **Have** | `MapTileEndpoints.cs:21`; MapServer `tile/{z}/{y}/{x}` |
| Pluggable schemes + LRU cache (+ persistent cache T-001) | served | **Have** | ADR-0046; `ITileScheme`/`ITileCache` |
| `tileInfo` LODs on the MapServer root matching the served scheme | served | **Have** | `MapTileScheme(schemes)` in `Maps.cs:Root`; single Web-Mercator scheme — the 24 served LODs replay G1's rows (resolution/scale in `CachedLodReplayTests`, envelope-equality in `TileEnvelopeProofTests`, T-048) |
| `exportTiles` (offline tile packages) + `estimateExportTileSize` | rejected | **Non-goal** | S2; mounted and rejected by name with typed `invalid.arguments` (ADR-0059); no packaging route, no job model (ADR-0033); root advertises `exportTilesAllowed:false` |
| WMTS (`WMTSCapabilities.xml`, `.../tile/{z}/{y}/{x}` WMTS addressing, RESTful + KVP) | rejected | **Non-goal** | S3 triple (base, capabilities, tile) mounted and rejected by name with typed `invalid.arguments` (ADR-0059); live tiles come from `tile/{z}/{y}/{x}` |
| Vector tiles (`.vtpk`, MVT endpoints) | — | **Non-goal** | raster tiles only; no MVT encoder, no `VectorTileServer`, no `.vtpk` packaging (ADR-0061; reconciles with ADR-0044/0033 first if ever reopened) |
| OGC API Tiles (`/tiles`, TileJSON, `capabilities` doc) | — | **Non-goal** | no TileJSON/landing-page/collections-tiles JSON; neutral tile routes + MapServer `tile/{z}/{y}/{x}` stay the surface (ADR-0061) |
| `storageInfo` / `exportTilesAllowed` / `maxExportTilesCount` honesty on root | served | **Have** | `exportTilesAllowed:false` on the root (ADR-0056); packaging fields correctly absent and the operations reject by name (ADR-0059) |

## 2. Same-data proof (wired by T-048)

- Envelope-equality of our `tile/{z}/{y}/{x}` against G1's cached LOD
  grid for the matching z/x/y (same Web-Mercator origin) is proven in
  `TileEnvelopeProofTests`: expected envelopes are derived from G1's own
  `tileInfo` (origin + LOD resolution × 256 px) and agree with
  `WebMercatorTileScheme.Bounds` to within half a pixel per zoom — pixels
  differ by style (ours) vs Esri cartography, so pixel-equality is never
  asserted. The MapServer tile route renders `ITileScheme.Bounds`
  directly, so the scheme-level proof covers the served envelope.
- `tileInfo` LOD table replay is proven in `CachedLodReplayTests`: our 24
  advertised LOD `resolution` values reproduce G1's rows tightly and the
  `scale` values within Esri's display rounding.

## 3. Follow-ups (closed by T-041/T-048)

- T-M Tiles parity: LOD table byte-proof vs G1, `exportTiles`+estimate scope
  (likely documented non-goal + honest reject, given no-job architecture),
  WMTS capabilities+tile scope, vector-tiles decision.
  Closed: LOD proof landed (`CachedLodReplayTests` + `TileEnvelopeProofTests`);
  `exportTiles`+estimate/WMTS/KML/jobs reject by name (ADR-0059, T-041);
  vector-tiles/MVT + OGC API Tiles are documented non-goals (ADR-0061).
