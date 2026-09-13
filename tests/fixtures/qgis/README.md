# QGIS WMS interop traces (T-013)

Real QGIS traffic captured against a live host and replayed verbatim by
`QgisReplayTests` in `tests/integration/Spatial.Host.Tests`. The next QGIS
round stays boring: any server change that breaks what QGIS actually sends
fails the replay suite first.

- **QGIS version:** 4.2.2-Belém do Pará (`qgis --version`), with Python
  bindings (`import qgis.core`) and `qgis_process` from the same install.
- **Capture:** `capture.py` (headless `qgis.core`: add-layer, canvas renders
  in EPSG:4326/3857, legend probe, provider identify on point/line/polygon)
  against a host seeded with a `qgis` map (point `cities` from the demo
  store, line `routes` and polygon `zones` ingested into the `memory`
  store). Exact request URLs come from the OGC request diagnostics log
  (commit `f670fec`), which records the verbatim parameters of every
  request. The machine-readable traces live in `qgis-4.2.2-wms.json`.
- **QGIS behaviours the replays pin:** add-layer `GetCapabilities` carries
  no `VERSION`; `GetMap` always carries `VERSION=1.3.0` with one empty
  `STYLES=` entry per layer plus QGIS extras (`DPI`, `MAP_RESOLUTION`,
  `FORMAT_OPTIONS`) the server must ignore; QGIS clips the `GetMap`
  `BBOX` to the layer extent and scales `WIDTH`/`HEIGHT` to match; a layer
  negotiated in EPSG:4326 is reprojected client-side, so the genuine
  `CRS=EPSG:3857` `GetMap` needs the layer negotiated in EPSG:3857;
  Identify issues `GetFeatureInfo` twice per click
  (`INFO_FORMAT=application/vnd.ogc.gml`, then `text/html`) with WMS 1.3.0
  `I`/`J` pixel addressing.

## Legend: no request to replay

QGIS issues **no** legend request against this server: the capabilities
advertise no `LegendURL` and the server has no `GetLegendGraphic`
operation (that gap is T-033). There is deliberately no legend replay;
when T-033 lands, extend `qgis-4.2.2-wms.json` with the captured
`GetLegendGraphic` trace and the replay suite covers it.

## Manual smoke step (GUI QGIS, ~5 minutes)

Headless QGIS drives every request above except the layer-tree legend
thumbnail, so after any OGC change:

1. Start a host, ingest `lines`/`polys` equivalents, publish the `qgis`
   map as in `QgisReplayTests` (or run `eng/seed.sh` and use its map).
2. In desktop QGIS 4.2.x: Layer → Add Layer → Add WMS/WMTS Layer → New —
   URL `http://<host>/ogc/qgis/wms` → Connect → Add all three layers.
3. Zoom to full extent (EPSG:4326), switch project CRS to EPSG:3857 and
   zoom again; confirm all three layers paint.
4. Identify (Ctrl+Shift+I) on a city point, a route line and a zone
   polygon; confirm each returns its feature.
5. Confirm the layer-tree legend (blank until T-033 lands).

## Headless QGIS in CI: assessed, not wired (T-013)

`qgis_process` cannot do this job: it runs processing algorithms, not WMS
data-provider traffic (no GetCapabilities/GetMap/GetFeatureInfo path).
Driving it headlessly works — `capture.py` proves `qgis.core` under
`QT_QPA_PLATFORM=offscreen` issues genuine provider traffic against the
live host — but wiring it into CI would add an apt-installed QGIS,
an XDG cache/profile, a live-host fixture and screenshot/identify
assertions for what the replay tests already pin deterministically at
the HTTP boundary. If the replays ever go green while GUI QGIS stays
broken, revisit: run `capture.py` against the review host and diff its
`URI:`/`IDENTIFY:` lines with the traces checked in here.
