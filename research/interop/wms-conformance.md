# WMS 1.3.0 conformance research (T-011)

- Check date: **2026-09-13** (every source below was fetched/read on this date; URLs are cited per claim).
- Scope: our WMS surface is `src/Spatial.Adapter.Ogc` — `WmsService.cs` serves only
  GetCapabilities, GetMap, GetFeatureInfo. Real-client testing keeps finding gaps
  (QGIS failed on every attempt; see T-013 and commits `f670fec` / `85cabcc`).
- Method: web research into authoritative + implementation corpora, then a catalogue of
  concrete implementable tests we do NOT have, each with protocol/version, exact request,
  expected assertion, real-client dependency, current engine behaviour with `file:line`,
  and rough effort. No production code changed here (`research/` stays out of `eng/verify.sh`).
- Process rule (owner mandate): every gap below is written as a failing-test specification
  (request + expected assertion + `file:line` of the current wrong behaviour), and every
  spawned follow-up task requires the red test before the fix.

## 1. Sources actually read

| # | Source | URL (checked 2026-09-13) | What was read |
|---|--------|--------------------------|---------------|
| S1 | OGC WMS 1.3.0 spec (OGC 06-042) landing page | https://portal.ogc.org/files/?artifact_id=14416 | Verified the normative artefact (redirects to `/is/06-042/06-042.pdf`). Section references below (§7.2/7.3/7.4, Annex E) are to OGC 06-042. |
| S2 | WMS 1.3.0 capabilities XSD (normative schema) | https://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd | Fetched (HTTP 200, ~21 KB); confirms `Style`, `LegendURL`, `EXCEPTIONS`/`INIMAGE` vocabulary and the `WMS_Capabilities` structure our `WmsCapabilities.cs` targets. |
| S3 | CITE ets-wms13 `getmap.xml` | https://raw.githubusercontent.com/opengeospatial/ets-wms13/master/src/main/scripts/ctl/getmap.xml | Full file (~181 KB): all `getmap:*` test names, assertions and KVP request shapes (incl. mixed-case param names, `STYLES=`, `EXCEPTIONS=INIMAGE/BLANK`, invalid FORMAT/LAYER/STYLE/CRS codes). |
| S4 | CITE ets-wms13 `getfeatureinfo.xml` | https://raw.githubusercontent.com/opengeospatial/ets-wms13/master/src/main/scripts/ctl/getfeatureinfo.xml | Full file (~53 KB): `info_format`, `i-and-j`, `query-layers` assertions; `InvalidFormat` / `InvalidPoint` / `LayerNotQueryable` codes. |
| S5 | CITE ets-wms13 `getcapabilities.xml` | https://raw.githubusercontent.com/opengeospatial/ets-wms13/master/src/main/scripts/ctl/getcapabilities.xml | Test names + assertions: schema validation, per-layer `BoundingBox`/`CRS`/`EX_GeographicBoundingBox`, style uniqueness, `UpdateSequence`. |
| S6 | CITE ets-wms13 `basic.xml`, `functions.xml`, `recommendations.xml` | https://github.com/opengeospatial/ets-wms13/tree/master/src/main/scripts/ctl | `basic:bgcolor` (default white), `basic:transparent-*`, `basic:layer-order`, `basic:aspect-ratio`, `bbox-exponential`. |
| S7 | CITE suite README (spec link) | https://raw.githubusercontent.com/opengeospatial/ets-wms13/master/README.md | Confirms the suite tests against OGC 06-042 (`portal.opengeospatial.org/files/?artifact_id=14416`). |
| S8 | GeoServer WMS reference (2.3.x branch) | https://raw.githubusercontent.com/geoserver/geoserver/2.3.x/doc/en/user/source/services/wms/reference.rst | `GetMap`/`GetFeatureInfo`/`GetLegendGraphic` op table; `EXCEPTIONS` values (`application/vnd.ogc.se_xml`, `...se_inimage`, `...se_blank`, `application/json`); `TIME` support note; `sld_body` param; GetFeatureInfo `info_format` optional / `feature_count` params. |
| S9 | GeoServer WMS vendor params (2.3.x) | https://raw.githubusercontent.com/geoserver/geoserver/2.3.x/doc/en/user/source/services/wms/vendor.rst | `cql_filter`, `tiled`, `buffer`, `angle` — vendor extensions we deliberately do NOT chase (no target client needs them). |
| S10 | MapServer msautotest WMS map (real request URLs) | https://raw.githubusercontent.com/MapServer/MapServer/main/msautotest/wxs/ows_wms.map | `RUN_PARMS` lines = verbatim GetFeatureInfo (plain + `application/vnd.ogc.gml`), GetMap 1.1.0 + 1.3.0, GetLegendGraphic, `EXCEPTIONS=INIMAGE` requests (quoted in §4). |
| S11 | MapServer expected outputs index | https://api.github.com/repos/MapServer/MapServer/contents/msautotest/wxs/expected?ref=main | Confirms corpora: `ows_wms_getfeatureinfo{,_plain,_gml}.xml`, `ows_wms_getlegendgraphic.xml`, `ows_wms_getmap{,_exception,_valid}.png`, capabilities 1.1.1 + 1.3.0. |
| S12 | QGIS Server WMS tests | https://raw.githubusercontent.com/qgis/QGIS/master/tests/src/python/test_qgsserver_wms.py | `GetStyles` operation, `SLD_BODY` on GetMap, `dpiMode=7` in WMS data sources (all three DPI params at once), `GetLegendGraphic` URL assembly, `DPI`/`MAP_RESOLUTION` handling. |
| S13 | QGIS WMS **client** provider (the code that calls servers like ours) | https://raw.githubusercontent.com/qgis/QGIS/master/src/providers/wms/qgswmsprovider.cpp | GetMap (§4 trace A), Identify/GetFeatureInfo (§4 trace B), GetLegendGraphic (§4 trace C), `DPI`/`MAP_RESOLUTION`/`FORMAT_OPTIONS` emission, `FEATURE_COUNT`, `TRANSPARENT=TRUE` (upper-case), I/J vs X/Y selection, axis inversion. |
| S14 | QGIS WMS capabilities parser | https://raw.githubusercontent.com/qgis/QGIS/master/src/providers/wms/qgswmscapabilities.cpp | `INFO_FORMAT → identify-format` mapping (the decisive QGIS interop fact, §3.1); `shouldInvertAxisOrientation` applies **only** for version 1.3.0/1.3. |
| S15 | GDAL WMS driver tests | https://raw.githubusercontent.com/OSGeo/gdal/master/autotest/gdrivers/wms.py | GDAL speaks **WMS 1.1.1** (`SRS=`, no `CRS=`; `version=1.1.1`; x-first `BBOX=-180,-90,180,90` for EPSG:4326) — the basis of gap G4. |
| S16 | OWSLib WMS client | https://raw.githubusercontent.com/geopython/OWSLib/master/owslib/wms.py | Factory defaults to **1.1.1** (`version='1.1.1'`), 1.3.0 opt-in — second witness that 1.1.1 must keep working. |
| S17 | deegree WMS service module | https://api.github.com/repos/deegree/deegree3/contents/deegree-services/deegree-services-wms?ref=3.4-main | Module exists (`pom.xml`, `src/main`, `src/test`); deegree is a full CITE-reference implementation — useful as a tiebreaker on spec disputes, not mined further (no target client is deegree-based). |
| S18 | pygeoapi docs | https://docs.pygeoapi.io/en/latest/ | Negative result: pygeoapi is OGC API (Features/Tiles/Maps), **no legacy WMS server** to mine — dropped as a source, recorded so nobody re-searches it. |

## 2. Current engine behaviour (baseline, all verified in-tree)

- Operations: `GETCAPABILITIES` / `GETMAP` / `GETFEATUREINFO` dispatched at
  `src/Spatial.Adapter.Ogc/WmsService.cs:35-44`; anything else (incl. `GetLegendGraphic`,
  `GetStyles`, `DescribeLayer`) → `OperationNotSupported` 400 (`WmsService.cs:42`).
- GetMap: parses viewport (`WmsService.cs:152-161`), format PNG/JPEG only
  (`WmsService.cs:177-181`), `transparent` TRUE/FALSE (`WmsService.cs:183-187`),
  `bgcolor` hex (`WmsService.cs:189-197`); **`STYLES`, `VERSION`, `EXCEPTIONS`, `BGCOLOR`-default,
  `DPI`/`MAP_RESOLUTION`/`FORMAT_OPTIONS`, `TIME`, `FEATURE_COUNT`, `SLD*` never read.**
- GetFeatureInfo: `query_layers` else `layers` (`WmsService.cs:79-80`); `I`/`J` with `X`/`Y`
  fallback, missing → viewport centre (`WmsService.cs:163-175`); `INFO_FORMAT` only
  `text/plain` + `application/json`/`geo+json`, else `NotSupported` 400 (`WmsService.cs:118-124`).
- CRS: only `EPSG:4326` (y-first, always), `CRS:84`, `EPSG:3857`, URN form accepted
  (`OgcCrs.cs:13-22`); axis swap applied unconditionally for EPSG:4326 (`WmsService.cs:155-157`).
- Capabilities: advertises `image/png`, `image/jpeg`, `text/plain`, `application/json`,
  exception format `XML` only (`WmsCapabilities.cs:40-44,95-102`); **no `<Style>` elements,
  no `LayerLimit`/`MaxWidth`/`MaxHeight`, no `UpdateSequence`, no dimensions.**
- Errors: `MissingParameterValue` / `InvalidParameterValue` / `LayerNotDefined` /
  `OperationNotSupported` / `NoApplicableCode` (`OgcServiceException.cs`, `OgcErrorMapper.cs`);
  **never** `InvalidFormat`, `InvalidPoint`, `StyleNotDefined`, `LayerNotQueryable`,
  `CurrentUpdateSequence`, `InvalidUpdateSequence`.
- Client-compat shims already present (do not regress): bare-`?` DCP URLs + repeated-param
  collapse (`WmsCapabilities.cs:106`, `OgcParameters.cs:CollapseRepeated` — `f670fec`);
  marker-radius click tolerance (`WmsService.cs:103-108` — `85cabcc`).

## 3. Confirm-or-dismiss verdicts (task body's list)

| # | Item | Verdict | Evidence |
|---|------|---------|----------|
| 3.1 | `INFO_FORMAT` coverage (`text/xml` / `application/vnd.ogc.gml` vs our plain+JSON) | **CONFIRMED gap (P1).** QGIS maps `text/plain`→Text, `text/html`→Html, and `application/json`→Feature alongside GML/`text/xml` (S14, lines ~625-648). So QGIS *can* identify against us today via plain/JSON, but any client fixed to `text/html` (QGIS Html identify), `text/xml`, or `application/vnd.ogc.gml` (MapServer S10, CITE S4 `each-info_format`) gets 400 `OperationNotSupported` (`WmsService.cs:118-124`). CITE also requires `InvalidFormat` code for bad values (S4) — we emit `OperationNotSupported`. | S4, S10, S13, S14 |
| 3.2 | `EXCEPTIONS` XML vs `INIMAGE`/`BLANK` | **CONFIRMED gap (P2).** We always return the XML envelope and never read `EXCEPTIONS`. CITE `getmap:exceptions-inimage-mime/-blank-red/-blank-transparent/-blank-mime` (S3) and MapServer `EXCEPTIONS=INIMAGE` traces (S10) require image/blank behaviours. GeoServer's MIME vocabulary is `application/vnd.ogc.se_xml` / `...se_inimage` / `...se_blank` (S8). | S3, S8, S10 |
| 3.3 | Transparency/alpha, PNG vs JPEG | **Mostly OK, one edge gap (P3).** `TRANSPARENT=TRUE` upper-case (what QGIS sends, S13) parses (WmsService.cs:183-187); PNG/JPEG negotiated (WmsService.cs:177-181). Gap: no BGCOLOR default (spec/CITE `basic:no-bgcolor` → white 0xFFFFFF, S6) — we pass `null` to the renderer; and JPEG+`TRANSPARENT=TRUE` is silently accepted (QGIS avoids sending it for JPEG, S13). | S6, S13 |
| 3.4 | `GetLegendGraphic` | **CONFIRMED gap (P2).** Returns 400 `OperationNotSupported` (`WmsService.cs:42`). QGIS requests per-layer legends with `SERVICE=WMS&VERSION=&SLD_VERSION=1.1.0&REQUEST=GetLegendGraphic&FORMAT=&LAYER=&STYLE=&TRANSPARENT=true[&DPI=]` (S13, §4 trace C); without it the QGIS layer tree shows no legend (non-fatal but visible). GeoServer (S8), MapServer (S10/S11) and QGIS Server (S12) all serve it. | S8, S10, S11, S12, S13 |
| 3.5 | `GetStyles` / `SLD=` / `SLD_BODY=` | **CONFIRMED, low priority (P3).** 400 today (`WmsService.cs:42`). QGIS Server implements `GetStyles` + honours `SLD_BODY` on GetMap (S12); SLD 1.1.0 POST bodies are a cartography-interop feature, not a map-rendering blocker — no target client in T-013 needs it. Defer until a client sends it (diagnostics from `f670fec` will show it). | S12 |
| 3.6 | `DPI` / `map_resolution` | **CONFIRMED, accept-and-ignore (P3).** QGIS sends `DPI` + `MAP_RESOLUTION` + `FORMAT_OPTIONS=dpi:..` together by default (`dpiMode=7`, S12 line ~582; S13 lines ~1347-1355). We ignore unknown params, so rendering works but at the wrong scale for DPI-aware maps. Fix = accept-and-ignore first (no 400 ever), scale-aware rendering later. | S12, S13 |
| 3.7 | EPSG:4326 axis order | **OK for 1.3.0, CONFIRMED gap for 1.1.1 (P1).** 1.3.0 lat-first swap is correct (`WmsService.cs:155-157`) and matches QGIS's `shouldInvertAxisOrientation` (S14). But the swap is applied **regardless of VERSION**, and GDAL (S15) + OWSLib (S16) speak **1.1.1**, where EPSG:4326 BBOX is lon/lat — we would mirror their maps. | S14, S15, S16 |
| 3.8 | `LAYERS` nesting / style selection | **CONFIRMED gap (P2).** `STYLES` is never read, so `LAYERS=a,b&STYLES=s1,s2` renders defaults (lenient = renders, but CITE `getmap:invalid-style` → `StyleNotDefined`, count-mismatch rules, and `two/three-styles` order semantics in S3 are all unimplemented). Capabilities advertise **zero** `<Style>` elements (`WmsCapabilities.cs`), so QGIS-style clients (`mActiveSubStyles`, S13) can never address a named style. | S3, S13 |
| 3.9 | Service exceptions on malformed params | **CONFIRMED gap (P1, codes wrong).** We *do* reject malformed input, but with the wrong codes: bad FORMAT → `InvalidParameterValue` not `InvalidFormat` (`WmsService.cs:177-181`); bad/non-numeric I/J → `InvalidParameterValue` not `InvalidPoint`, and *missing* I/J silently uses the centre (`WmsService.cs:163-175`); inverted/degenerate BBOX renders instead of raising (S3 `bbox-minx-gt-maxx` et al.); GetMap without VERSION → 200 instead of an exception (S3 `getmap:no-version`). | S3, S4 |
| 3.10 | `TIME` / WMS-T | **DISMISSED (no target client needs it).** QGIS only sends `TIME` when the server advertises temporal capabilities (`temporalCapabilities()->hasTemporalCapabilities()`, S13 `addWmstParameters`); we advertise none, so no QGIS/GDAL/MapServer flow sends it. GeoServer documents `TIME` (S8) — revisit if a time-aware client appears. | S8, S13 |

## 4. Real client request traces (verbatim shapes)

Trace A — **QGIS Desktop GetMap** (reconstructed from S13 `qgswmsprovider.cpp`, lines ~1317-1370;
axis handling line ~1287; DPI lines ~1347-1355; QGIS master, checked 2026-09-13):
```text
GET {DCP-GetMap-URL}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap
  &BBOX=<minY,minX,maxY,maxX when EPSG:4326 1.3.0; else minX,minY,maxX,maxY>
  &CRS=EPSG:4326&WIDTH=<px>&HEIGHT=<px>
  &LAYERS=<visible layers, comma-joined>&STYLES=<sub-styles, comma-joined>
  &FORMAT=image/png&TRANSPARENT=TRUE
  [&DPI=<dpi>&MAP_RESOLUTION=<dpi>&FORMAT_OPTIONS=dpi:<dpi>]   # all three when dpiMode=7 (default)
  [&OPACITIES=..][&FILTER=..][&TIME=..]                        # only when set
```
Notes: `TRANSPARENT` is upper-case `TRUE`; QGIS omits it for JPEG; `STYLES=` may be empty
(all-default); QGIS honours the advertised DCP URL (hence `f670fec`'s bare-`?` fix).

Trace B — **QGIS Desktop Identify → GetFeatureInfo** (S13 lines ~3542-3570; 1.3.0 branch):
```text
GET {DCP-GetFeatureInfo-URL}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo
  &BBOX=<same axis rule>&CRS=EPSG:4326&WIDTH=<ctx>&HEIGHT=<ctx>
  &LAYERS=<single>&STYLES=<single>&FORMAT=<map image format>
  &QUERY_LAYERS=<single>&INFO_FORMAT=<mapped: text/plain|text/html|application/json|...gml>
  &I=<col>&J=<row>[&FEATURE_COUNT=<n>]                        # X=/Y= when version is 1.1.1
```
Notes: one request per queryable visible layer; `INFO_FORMAT` comes from the caps-advertised
set via the S14 mapping; default `featureCount=10` (S12 line ~582 shows the stored source).

Trace C — **QGIS layer-tree GetLegendGraphic** (S13 lines ~4456-4495):
```text
GET {base-or-advertised-URL}?SERVICE=WMS&VERSION=1.3.0&SLD_VERSION=1.1.0
  &REQUEST=GetLegendGraphic&FORMAT=image/png
  &LAYER=<first active sublayer>&STYLE=<first active substyle>&TRANSPARENT=true
  [&DPI=<defaultLegendGraphicResolution>][&BBOX=..&CRS=..]     # BBOX/CRS only for contextual legends
```
Falls back to the layer's caps `LegendURL` when present (S13 lines ~417-449) — advertising
`LegendURL` per style is a cheaper partial fix than a full GetLegendGraphic renderer.

Trace D — **MapServer msautotest** (verbatim `QUERY_STRING`s from S10 `ows_wms.map`, checked 2026-09-13):
```text
# GetFeatureInfo plain + GML (note FEATURE_COUNT=5, STYLES=, BGCOLOR, lat-first EPSG:4326 BBOX):
SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&CRS=EPSG%3A4326&BBOX=35.18,-141.0,90.81,-52.0
&WIDTH=560&HEIGHT=350&LAYERS=road&STYLES=&FORMAT=image%2Fpng&BGCOLOR=0xFFFFFF&TRANSPARENT=FALSE
&QUERY_LAYERS=road&INFO_FORMAT=text%2Fplain&I=483&J=291&FEATURE_COUNT=5
# same with INFO_FORMAT=application%2Fvnd.ogc.gml
# GetMap 1.1.0 (note SRS=, VERSION=1.1.0):
SERVICE=WMS&VERSION=1.1.0&REQUEST=GetMap&SRS=EPSG:4326&BBOX=-67.5725,42.3683,-58.9275,48.13
&FORMAT=image/png&WIDTH=300&HEIGHT=200&STYLES=&LAYERS=road
# GetLegendGraphic 1.1.1:
SERVICE=WMS&x=500&y=300&LAYER=popplace&FORMAT=agg/png&VERSION=1.1.1&REQUEST=GetLegendGraphic
&STYLES=&EXCEPTIONS=application%252Fvnd.ogc.se_inimage&SRS=EPSG:42304&BBOX=-2200000,-712631,3072800,3840000&WIDTH=600&HEIGHT=600
# EXCEPTIONS=INIMAGE GetMap (expects PNG bytes, application/vnd.ogc.se_inimage on error):
SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS=road&CRS=EPSG%3A4326&BBOX=35.18,-141.0,90.81,-52.0
&WIDTH=560&HEIGHT=350&STYLES=&FORMAT=image%2Fpng&BGCOLOR=0xFFFFFF&TRANSPARENT=FALSE&EXCEPTIONS=INIMAGE
```

Trace E — **GDAL WMS driver** (S15 `wms.py`, checked 2026-09-13): always 1.1.1 KVP —
`...?SERVICE=WMS&request=GetMap&version=1.1.1&layers=og:bugsites&styles=&srs=EPSG:26713&bbox=...`
and subdataset form
`WMS:https://host/twms.cgi?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=..&SRS=EPSG:4326&BBOX=-180,-90,180,90`.
Any GDAL-backed client (incl. QGIS's GDAL provider path) exercises our 1.1.1 axis-order gap G4.

## 5. Implementable test catalogue (all missing today)

Conventions: `P1` = blocks a real client; `P2` = CITE conformance / visible degradation;
`P3` = accept-and-ignore or deferred. Effort: S ≤0.5d, M 1-2d, L 3d+ (incl. red test + fix + docs).

### G1. GetFeatureInfo `INFO_FORMAT=text/html` → 400 `OperationNotSupported` (P1, M)
- Protocol: WMS 1.3.0 GetFeatureInfo. CITE: `getfeatureinfo:each-info_format` (S4) requires
  every *advertised* format to round-trip with matching MIME; QGIS Html-identify needs it (S14).
- Request: `SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&CRS=CRS:84&BBOX=0,-0.002,0.004,0`
  `&WIDTH=200&HEIGHT=100&LAYERS=<l>&QUERY_LAYERS=<l>&INFO_FORMAT=text/html&I=10&J=10`
  (CITE shape, S4 `each-info_format`).
- Expected: 200 `text/html` body describing the feature (GeoServer/MapServer serve HTML tables).
- Current: 400 `OperationNotSupported` — `WmsService.cs:118-124` (`WriteFeatureInfo` switch).
- Red-test spec: host-level test asserting 200 + `text/html` content-type for the above request
  (plus `text/xml` and `application/vnd.ogc.gml` variants — same gap, one task).

### G2. Invalid `INFO_FORMAT` / `FORMAT` / `I` / `J` / style codes are wrong (P1, S)
- Protocol: WMS 1.3.0 GetMap/GetFeatureInfo. CITE: `getmap:invalid-format` → `InvalidFormat`
  (S3); `getfeatureinfo:invalid-info_format` → `InvalidFormat`, `invalid-i/j` → `InvalidPoint`
  (S4); `getmap:invalid-style` → `StyleNotDefined` (S3).
- Requests: `...REQUEST=GetMap...&FORMAT=UnknownFormat` (S3 shape, mixed-case names);
  `...REQUEST=GetFeatureInfo...&INFO_FORMAT=UnknownFormat`; `...&I=abc&J=10`.
- Expected: ServiceExceptionReport with the CITE code (`InvalidFormat` / `InvalidPoint` /
  `StyleNotDefined`), not our generic `InvalidParameterValue` / `OperationNotSupported`.
- Current: `WmsService.cs:177-181` (FORMAT→`Invalid`), `WmsService.cs:118-124` (INFO_FORMAT→
  `NotSupported`), `WmsService.cs:221-229` (I/J→`Invalid` via `OptionalNumber`).
- Red-test spec: three host tests asserting the exception `code` attribute for the three requests.

### G3. Degenerate/inverted BBOX renders instead of raising (P2, S)
- Protocol: WMS 1.3.0 GetMap. CITE `getmap:bbox-minx-gt-maxx/-minx-eq-maxx/-miny-gt-maxy/-miny-eq-maxy`
  (S3): server **shall** throw a Service Exception.
- Request: valid GetMap with `BBOX=10,10,5,5` (minx>maxx) and `BBOX=5,5,5,10` (zero width).
- Expected: 4xx ServiceExceptionReport (CITE checks `ServiceExceptionReport`, code `InvalidParameterValue`
  acceptable here — S3 asserts the envelope, not the code).
- Current: no BBOX sanity check — `WmsService.cs:152-161` (`ParseViewport`) renders whatever bbox arrives.
- Red-test spec: host tests for the four CITE shapes asserting non-200 + `ServiceExceptionReport` root.

### G4. WMS 1.1.1 `EPSG:4326` BBOX mirrored (P1, M)
- Protocol: WMS **1.1.1** GetMap/GetFeatureInfo. GDAL (S15) and OWSLib (S16, default `version='1.1.1'`)
  send `SRS=EPSG:4326&BBOX=<lon>,<lat>` (x-first); WMS 1.1.1 §7 (axis order follows EPSG database
  lon-first) agrees; QGIS only inverts for 1.3.0/1.3 (S14 `shouldInvertAxisOrientation`).
- Request: `SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&SRS=EPSG:4326&BBOX=-67.57,42.36,-58.92,48.13`
  `&FORMAT=image/png&WIDTH=300&HEIGHT=200&STYLES=&LAYERS=<l>` (MapServer S10 shape).
- Expected: map of the lon/lat window (same geography as the 1.3.0 lat/lon equivalent).
- Current: `OgcCrs.cs:13-22` + `WmsService.cs:155-157` swap y-first for EPSG:4326 **ignoring VERSION** —
  the 1.1.1 window is mirrored about y=x. (VERSION itself is never validated; CITE
  `getmap:no-version` (S3) expects an exception for a *missing* version — bundled here.)
- Red-test spec: two host tests, identical geography via 1.1.1 (`SRS`, lon/lat) and 1.3.0
  (`CRS`, lat/lon), asserting pixel-identical (or envelope-identical) renders.

### G5. `STYLES` ignored; no named styles advertised (P2, M)
- Protocol: WMS 1.3.0 GetMap. CITE `getmap:styles-*` family (S3): `STYLES=` (single null),
  `STYLES=,,,` (multi null), mixed `style1,,style2,,`, invalid → `StyleNotDefined`; QGIS sends
  per-layer joined sub-styles (S13) parsed against caps `<Style>` (S14).
- Request: `...REQUEST=GetMap&LAYERS=<a>,<b>&STYLES=<bogus>,` and `...&LAYERS=<a>&STYLES=`.
- Expected: `StyleNotDefined` for the bogus name; 200 for the default-style form.
- Current: `STYLES` never read (`WmsService.cs:59-75`); capabilities carry no `<Style>`
  (`WmsCapabilities.cs:52-79` builds Name/Title/CRS/BBox only). Wrong-count STYLES also unvalidated.
- Red-test spec: host tests for bogus-style→`StyleNotDefined`, `STYLES=`→200,
  `LAYERS=a,b&STYLES=s1` (count mismatch)→exception; caps test asserting one default `<Style>` per layer.

### G6. `GetLegendGraphic` → 400 (P2, M)
- Protocol: WMS 1.3.0 `GetLegendGraphic` (SLD profile §, served by GeoServer S8, MapServer S10,
  QGIS Server S12; requested by QGIS layer tree, §4 trace C).
- Request: `SERVICE=WMS&VERSION=1.3.0&SLD_VERSION=1.1.0&REQUEST=GetLegendGraphic&FORMAT=image/png`
  `&LAYER=<l>&STYLE=&TRANSPARENT=true` (S13 shape).
- Expected: 200 legend PNG for the layer's persisted style (MapServer S11 `ows_wms_getlegendgraphic.xml`
  shape: mime + image bytes).
- Current: 400 `OperationNotSupported` — `WmsService.cs:35-44` dispatch switch.
- Red-test spec: host test with the trace-C request asserting 200 + `image/png`; cheap partial
  alternative (separate P3): advertise per-style `LegendURL` pointing at a new legend endpoint.

### G7. `EXCEPTIONS=INIMAGE|BLANK` ignored (P2, M)
- Protocol: WMS 1.3.0 GetMap. CITE `getmap:exceptions-inimage-mime/-blank-red/-blank-transparent/-blank-mime`
  (S3); MapServer trace D `EXCEPTIONS=INIMAGE` (S10); GeoServer MIME vocabulary (S8).
- Request: failing GetMap (e.g. `LAYERS=<bogus>`) with `&EXCEPTIONS=INIMAGE`, and with
  `&EXCEPTIONS=BLANK&TRANSPARENT=TRUE`; default (absent) and `EXCEPTIONS=XML` control cases.
- Expected: INIMAGE → 200 with requested image MIME (exception painted in); BLANK+TRANSPARENT → all-transparent
  image; default/XML → `text/xml` ServiceExceptionReport (S3 `exceptions-default`).
- Current: `EXCEPTIONS` never read; always XML (`WmsService.cs`, `WmsCapabilities.cs:44` advertises `XML` only).
- Red-test spec: host tests for the three shapes asserting content-type + (for BLANK) pixel transparency.

### G8. `FEATURE_COUNT` ignored (P3, S)
- Protocol: WMS 1.3.0 GetFeatureInfo. QGIS sends `FEATURE_COUNT` (default 10, S12/S13 line ~3568);
  MapServer traces use `FEATURE_COUNT=5` (S10); GeoServer documents `feature_count` (S8).
- Request: trace-D GetFeatureInfo with `&FEATURE_COUNT=1` over a layer with ≥2 features near the click.
- Expected: at most 1 feature in the response.
- Current: never read — `WmsService.cs:76-95` returns all matches.
- Red-test spec: host test asserting response feature count ≤ FEATURE_COUNT.

### G9. `DPI` / `MAP_RESOLUTION` / `FORMAT_OPTIONS` unknown-but-tolerated (P3, S)
- Protocol: WMS 1.3.0 GetMap + GetLegendGraphic. QGIS sends all three by default (S12 `dpiMode=7`;
  S13 lines ~1347-1355, ~1566-1574, ~4473-4487).
- Request: valid GetMap + `&DPI=90&MAP_RESOLUTION=90&FORMAT_OPTIONS=dpi:90` (S13 shape).
- Expected (now): 200 — vendor params accepted and ignored, never a 400. (Scale-aware rendering later.)
- Current: already 200 via unknown-param tolerance — **no code change needed**, but no test pins it;
  a future strict-validator could regress QGIS default traffic.
- Red-test spec: host test with the exact QGIS param triple asserting 200 + valid PNG.

### G10. Missing `VERSION` on GetMap → 200 (P2, S)
- Protocol: WMS 1.3.0 GetMap. CITE `getmap:no-version` (S3): request without VERSION → exception report.
- Request: valid GetMap minus `VERSION` (S3 `no-version` shape).
- Expected: 4xx ServiceExceptionReport.
- Current: 200 — VERSION never read (`WmsService.cs:59-75`).
- Red-test spec: host test asserting non-200 + `ServiceExceptionReport` for versionless GetMap.
  (Bundled with G4's VERSION handling; kept separate so G4 can land axis-order first.)

### G11. `BGCOLOR` default + JPEG/`TRANSPARENT=TRUE` edge (P3, S)
- Protocol: WMS 1.3.0 GetMap. CITE `basic:no-bgcolor` → uncovered pixels are white 0xFFFFFF (S6);
  `basic:blue-bgcolor` (S6); QGIS never sends TRANSPARENT with JPEG (S13 lines ~1357-1361).
- Requests: GetMap over a partially-covering bbox with no BGCOLOR; same with `&FORMAT=image/jpeg&TRANSPARENT=TRUE`.
- Expected: background `#FFFFFF`; JPEG+TRANSPARENT either honoured-opaque or `InvalidParameterValue`
  (pinned behaviour, not a 500).
- Current: bgcolor `null` passed to renderer (`WmsService.cs:59-75`); JPEG+TRANSPARENT silently accepted.
- Red-test spec: pixel-assertion test for the white default; behaviour-pinning test for JPEG+TRANSPARENT.

### G12. `QUERY_LAYERS` ⊄ `LAYERS`, and non-queryable layers (P2, S)
- Protocol: WMS 1.3.0 GetFeatureInfo. CITE `less-query_layers` → valid when QUERY_LAYERS ⊂ LAYERS (S4);
  `query_layers-not-queryable` → `LayerNotQueryable` (S4); `invalid-query_layers` → `LayerNotDefined` (S4).
- Requests: `&LAYERS=<a>,<b>&QUERY_LAYERS=<a>` (must 200); QUERY_LAYERS naming a non-queryable layer.
- Expected: subset → 200; non-queryable → `LayerNotQueryable`.
- Current: subset works by accident (`WmsService.cs:79-80` only reads QUERY_LAYERS); `LayerNotQueryable`
  can never be emitted (all layers `queryable=1`, no code path references it); unknown names → 
...[truncated 2086 chars]