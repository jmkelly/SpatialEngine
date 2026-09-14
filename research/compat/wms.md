# WMS compatibility matrix (OGC 1.3.0 + Esri WMSServer)

Sources (checked 2026-09-14):
- S1 OGC WMS 1.3.0 (06-042): https://portal.ogc.org/files/?artifact_id=14416
- S2 Capabilities XSD: https://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd
- S3 CITE ets-wms13 (`getmap.xml` ~181KB, `getfeatureinfo.xml` ~53KB,
  `getcapabilities.xml`, `basic/functions/recommendations`):
  https://github.com/opengeospatial/ets-wms13/tree/master/src/main/scripts/ctl
- S4 GeoServer WMS reference+vendor: https://raw.githubusercontent.com/geoserver/geoserver/2.3.x/doc/en/user/source/services/wms/reference.rst
- S5 MapServer msautotest (`ows_wms.map` verbatim URLs, expected outputs index)
- S6 QGIS provider+caps parser (client traces A/B/C) + QGIS Server tests
  (`GetStyles`, `SLD_BODY`, `dpiMode=7`, `DPI`+`MAP_RESOLUTION`+`FORMAT_OPTIONS`)
- S7 GDAL WMS tests (speaks **1.1.1**, `SRS=`, x-first EPSG:4326) + OWSLib (defaults 1.1.1)
- Full detail: `research/interop/wms-conformance.md` (T-011, 18 sources, 10 verdicts)

Our surface: `src/Spatial.Adapter.Ogc/WmsService.cs` (dispatch + 4 ops),
`WmsCapabilities.cs`, `OgcCrs.cs`, `OgcParameters.cs`, `OgcServiceException.cs`.

## 1. Operations

| Capability | Ours | Status | Evidence |
|---|---|---|---|
| `GetCapabilities` (1.3.0, per-layer `BoundingBox`/`CRS`/`EX_GeographicBoundingBox`, styles, `UpdateSequence`) | served | **Have** | `WmsService.cs:HandleAsync`; `WmsCapabilities.cs` |
| `GetMap` (PNG/JPEG, `TRANSPARENT`, `BGCOLOR` default white, `EXCEPTIONS` XML/INIMAGE/BLANK, `DPI`/`MAP_RESOLUTION`/`FORMAT_OPTIONS` accept-and-ignore, `STYLES=`/default, 1.1.1 axis rule) | served | **Have** | `WmsService.cs:RenderMapAsync`; closed by T-029/T-030/T-031 + host notes in `host-and-clients.md` |
| `GetFeatureInfo` (`text/plain+html+xml`, `application/vnd.ogc.gml`, JSON, GML; `FEATURE_COUNT` cap; `InvalidFormat/InvalidPoint/LayerNotQueryable` codes; BBOX validation) | served | **Have** | `WmsService.cs:WriteFeatureInfo` + `GetFeatureInfoAsync` (T-029/T-030) |
| `GetLegendGraphic` (per-layer style PNG + `LegendURL` per style) | served | **Have** | `WmsService.cs:GetLegendGraphicAsync` (host notes) |
| `GetStyles` | — | **Missing** | S6 QGIS Server implements; we 400 `OperationNotSupported` (`WmsService.cs:HandleAsync` default arm) |
| `DescribeLayer` (SLD association) | — | **Missing** | same default arm; no SLD model |
| `SLD=` / `SLD_BODY=` on GetMap | — | **Missing** | never read; no target client sends them yet (T-014 deliberate non-goal, revisit on trace) |
| Full 1.1.1 capabilities dialect (1.1.1 `WMS_Capabilities` shape, `SRS=` vocab) | — | **Partial** | 1.1.1 GetMap/GetFeatureInfo KVP works (T-031); caps still emit the 1.3.0 dialect (T-014 non-goal) |
| `TIME`/WMS-T (dimensions advertised + subsetting) | — | **Non-goal** | we advertise no temporal caps so no client sends `TIME` (verdict 3.10); revisit if a time-aware client appears |
| Scale-aware DPI rendering (QGIS `dpiMode=7` triple) | accept-and-ignore | **Partial** | params accepted, rendering stays 96dpi-equivalent (T-014 deliberate) |
| Vendor params (`CQL_FILTER`, `tiled`, `buffer`, `angle`, …) | — | **Non-goal** | GeoServer-only (S4 vendor.rst); no target client needs them |
| JPEG+`TRANSPARENT` leniency, `DPI`/`MAP_RESOLUTION`/`FORMAT_OPTIONS` ignore, absent-`BGCOLOR`→white | served | **Have** | host-notes behaviour pins |

## 2. Same-data proof

- CITE-shaped replay tests at host level (`getmap:*`, `getfeatureinfo:*`
  assertions) — red-first per T-029/T-030/T-031.
- QGIS Desktop traces A (GetMap), B (Identify), C (Legend) reconstructed from
  `qgswmsprovider.cpp` in `wms-conformance.md` §4; our diagnostics hook
  (`f670fec`) records live client URLs for regression.
- GDAL/OWSLib 1.1.1 traffic pinned by T-031 envelope-identical render test.

## 3. Follow-ups (filed)

- T-J WMS remaining surface: `GetStyles`+`DescribeLayer`+`SLD/SLD_BODY` scope,
  full 1.1.1 capabilities dialect, scale-aware DPI (one scoping task; implement
  only what a recorded client trace demands — diagnostics first).
