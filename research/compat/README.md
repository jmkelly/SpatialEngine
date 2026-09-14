# Service compatibility matrices vs ArcGIS Server

Check date: **2026-09-14**. Method: authoritative ArcGIS REST API reference
(`developers.arcgis.com/rest/services-reference/enterprise/`, fetched 2026-09-14),
OGC schemas/suites for WMS/WFS, live ground-truth probes of public ArcGIS
Servers (responses frozen under `ground-truth/`, manifest dated 2026-09-14),
and the in-tree implementation (`src/Spatial.Adapter.GeoServices`,
`src/Spatial.Adapter.Ogc`, `src/Spatial.Host/Api`).

## Services (one matrix each)

| # | Our service | ArcGIS / OGC counterpart | Matrix | Verdict |
|---|-------------|--------------------------|--------|---------|
| 1 | FeatureServer | Feature Service (`feature-service/`, `layer-feature-service/`, `query-feature-service-layer/`) | `feature-service.md` | Core served; modern query params + attachments/sync/replicas are the gap |
| 2 | MapServer | Map Service (`map-service/`, `export-map/`, `identify-map-service/`, `find/`, …) | `map-service.md` | Core served; legend/dynamicLayers/tile-export/WMTS are the gap |
| 3 | ImageServer | Image Service (`image-service/`, `export-image/`, …) | `image-service.md` | Core served; mosaic/render rules honestly rejected; legend/find/tile-export are the gap |
| 4 | GeometryServer | Geometry Service (`geometry-service/` + 21 op pages) | `geometry-service.md` | 14/21 ops served; edit-topology ops + coordinate-notation ops are the gap |
| 5 | WMS | OGC WMS 1.3.0 (+ Esri `WMSServer`) | `wms.md` | Core + QGIS gaps closed; GetStyles/SLD, 1.1.1 caps dialect, scale-DPI remain |
| 6 | WFS | OGC WFS 2.0.0 (+ Esri `WFSServer`) | `wfs.md` | Read-path served as GeoJSON; GML/sort/paging/filter + write path are the gap |
| 7 | Tiles | Cached Map/Image tile + `exportTiles`/`tile-map` + WMTS | `tiles.md` | Web-Mercator tiles served; scheme negotiation + offline tile packages are the gap |
| 8 | Catalog / Admin / other servers | `catalog/`, Geocode, GP, Network, GeoEvent… | `scope.md` | Catalog served; Geocode/GP/etc. are explicit non-goals |

Prior art (not duplicated, referenced): `architecture/references/geoservices-compatibility.md`
(2010 v1.0 baseline + T-015 closeout non-goals),
`research/arcgis/conformance-sources.md` (T-012, 15 serve/consume tests),
`research/interop/wms-conformance.md` (T-011, WMS CITE/QGIS/GDAL traces).

## Ground truth (proving we return the same data)

- `ground-truth/` — 12 frozen ArcGIS Server responses captured 2026-09-14
  (Feature root/layer/count/ids/extent, Map root/layers/legend, cached
  `tileInfo` with 24 LODs, Image root, Geometry root, services catalog).
  Each matrix cites the file it replays.
- `tests/fixtures/arcgis/captured/` — 52-endpoint recorded corpus (316 layers)
  replayed by `RealWorldFixtureTests.cs` on the consume path.
- `clients/typescript/test/geoservices-e2e.test.ts` — live host driven by the
  official `@esri/arcgis-rest-*` client libraries (serve-path proof).

Every gap row below names the status **Have / Partial / Missing / Non-goal**,
the in-tree evidence (`file:line`), and the follow-up task (all filed in
`eng/tasks`, area `interop.*`). Non-goals are rejected by name in code, never
silently ignored.

## Confidence log (the loop, until 99%)

1. Pass 1 (2026-09-14): enumerated the reference surface from the fetched
   `.../enterprise/<op>/` link graph (Feature ~60 ops, Map ~40, Image ~60,
   Geometry 21), diffed against the route tables
   (`GeoServicesEndpoints*.cs`, `Ogc/*Service.cs`, `Host/Api/*Tile*.cs`),
   probed 12 live responses. Confidence ~85%: param-level detail per op page
   not yet extracted.
2. Pass 2 (2026-09-14): verified each matrix row against code (`grep` for every
   `parameters.Get("…")` and every dispatched operation name), cross-checked
   the frozen ground-truth keys against our response models, closed the loop
   on the T-015/T-011/T-012 non-goal lists so nothing is double-filed or
   dropped. Confidence ~99%: remaining 1% is per-field enterprise-version
   drift (11.x/12.x flags), which the follow-up tasks pin with live replay
   tests.
