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
| `tileInfo` LODs on the MapServer root matching the served scheme | served | **Partial** | `MapTileScheme(schemes)` in `Maps.cs:Root`; single Web-Mercator scheme only — G1 advertises 24 LODs over the same scheme family, but ArcGIS LOD `scale/resolution` tables are not byte-compared |
| `exportTiles` (offline tile packages) + `estimateExportTileSize` | — | **Missing** | S2; no packaging route; no job model (ADR-0033) |
| WMTS (`WMTSCapabilities.xml`, `.../tile/{z}/{y}/{x}` WMTS addressing, RESTful + KVP) | — | **Missing** | S3; zero WMTS routes in tree |
| Vector tiles (`.vtpk`, MVT endpoints) | — | **Missing** | S2 `v inequality`; we render raster tiles only |
| OGC API Tiles (`/tiles`, TileJSON, `capabilities` doc) | — | **Partial** | neutral `GET /api/maps/{name}/tiles/0/0/0.png` discovery link exists (`DiscoveryPage.cs:214`); no OGC-API-Tiles JSON |
| `storageInfo` / `exportTilesAllowed` / `maxExportTilesCount` honesty on root | — | **Partial** | G1 lists them; we don't emit packaging fields (correctly absent, but not advertised as unsupported) |

## 2. Same-data proof (to be wired by T-M)

- Byte-compare our `tile/{z}/{y}/{x}` against G1's cached LOD grid for the
  matching z/x/y (same Web-Mercator origin): envelopes must agree; pixels
  differ by style (ours) vs Esri cartography — envelope-equality, not
  pixel-equality, is the assertion.
- `tileInfo` LOD table replay: our advertised LOD `resolution/scale` values
  must reproduce G1's row for the overlapping levels.

## 3. Follow-ups (filed)

- T-M Tiles parity: LOD table byte-proof vs G1, `exportTiles`+estimate scope
  (likely documented non-goal + honest reject, given no-job architecture),
  WMTS capabilities+tile scope, vector-tiles decision.
