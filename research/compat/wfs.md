# WFS compatibility matrix (OGC WFS 2.0.0 + Esri WFSServer)

Sources (checked 2026-09-14):
- S1 OGC WFS 2.0.0 (09-025r2): https://portal.ogc.org/files/?artifact_id=39967
  (ops: `GetCapabilities`, `DescribeFeatureType`, `GetFeature`, `GetPropertyValue`,
  `ListStoredQueries`/`DescribeStoredQueries`, `CreateStoredQuery`/`DropStoredQuery`,
  `Transaction`, `LockFeature`, `GetFeatureWithLock`)
- S2 OGC CITE ets-wfs20: https://github.com/opengeospatial/ets-wfs20
  (basic/getfeature/describefeaturetype/lockfeature/transaction suites)
- S3 GeoServer WFS reference (output formats, `srsName`, paging, sorting, CQL/ECQL):
  https://docs.geoserver.org/stable/en/user/services/wfs/reference.html
- S4 QGIS WFS provider (the client that calls servers like ours):
  https://raw.githubusercontent.com/qgis/QGIS/master/src/providers/wfs/qgswfsprovider.cpp
  (paged `GetFeature` with `STARTINDEX`, `outputFormat=application/json`, GML default)
- S5 GDAL WFS driver (consumes `GetFeature`, paging, `srsName` handling):
  https://gdal.org/en/stable/drivers/vector/wfs.html
- S6 pygeoapi OGC provider (reads our WFS as a client would):
  https://docs.pygeoapi.io (WFS 2.0.0 `GetFeature` paging via `count`/`startIndex`)

Our surface: `src/Spatial.Adapter.Ogc/WfsService.cs` (3 ops),
`WfsCapabilities.cs`, `WfsSchema.cs`, `GeoJson.cs`, `OgcGeometry.cs`, `OgcCrs.cs`.

## 1. Operations

| Capability | Ours | Status | Evidence |
|---|---|---|---|
| `GetCapabilities` 2.0.0 | served | **Have** | `WfsService.cs:CapabilitiesAsync`; `WfsCapabilities.cs` |
| `DescribeFeatureType` (XSD per feature layer) | served | **Have** | `WfsService.cs:DescribeAsync`; `WfsSchema.cs` |
| `GetFeature` basic (typenames select, bbox subset with CRS, `count` cap → `MaxFeatures`, GeoJSON out) | served | **Have** | `WfsService.cs:GetFeatureAsync:61-80`, `ReadAsync`, `Project` |
| `outputFormat=application/geo+json\|json` (+ absent = GeoJSON) | served | **Have** | `EnsureGeoJson` (`WfsService.cs:107-127`) |
| GML output (`application/gml+xml`, `text/xml; subtype=gml/3.2`) | honestly rejected | **Missing** | `EnsureGeoJson` throws `InvalidParameterValue` naming geo+json; QGIS/GDAL default to GML — the top interop gap |
| `srsName` (reproject response) | — | **Missing** | never read; bbox input is projected (`Project`), output stays in layer CRS |
| `startIndex` (keyset/offset paging; QGIS/GDAL page with it) | — | **Missing** | `Count()` reads `count` only (`WfsService.cs:129-134`); no `startIndex` → clients can only read page 1 |
| `sortBy` (server-side ordering) | — | **Missing** | never read; ordering is store/ingest order |
| Attribute/temporal/spatial filtering (`filter` FES XML, `cql_filter`, RESOURCEID/BBOX pre-computed) | — | **Missing** | only `bbox` is honoured; no FES/CQL parser |
| `GetPropertyValue` (value-only projection) | — | **Missing** | default arm `NotSupported` (`WfsService.cs:25-31`) |
| Stored queries (`ListStoredQueries`, `DescribeStoredQueries`, `CreateStoredQuery`, `DropStoredQuery`) | — | **Missing** | same default arm |
| `Transaction` (insert/update/delete/replace) + `LockFeature`/`GetFeatureWithLock` | — | **Non-goal** | write path belongs to the gated Esri edit verbs + neutral ingest (ADR-0037/0041); WFS-T would duplicate them. Revisit only if a WFS-T client appears |
| `outputFormat` GML in `DescribeFeatureType` variants, `aliases`, `resolve`/`resolveDepth`, `paging` (`next` links) | — | **Missing** | minimal XSD + bare GeoJSON collection, no `numberMatched/numberReturned/next` envelope |
| WFS 1.0.0/1.1.0 dialects (`maxFeatures`, `SRSNAME`, GML2) | — | **Non-goal** | we advertise 2.0.0 only; GDAL negotiates down at its own risk — revisit on trace |

## 2. Same-data proof (wired by T-047)

- QGIS WFS provider replay (S4): `WfsPageThroughTests.Qgis_provider_page_through`
  issues `GetFeature&outputFormat=application/json&count=<max>&STARTINDEX=n`
  against a 7-feature layer with `MaxFeatures=3` and terminates with the
  exact `numberMatched` total in stable feature-id order, no duplicates.
- GDAL WFS driver replay (S5): `WfsPageThroughTests.Gdal_driver_page_through`
  pages the same layer driving the loop off the envelope `next` links and
  terminates with the same total, order and duplicate-free set; a `count`
  above the max clamps to `MaxFeatures` with `next` present, so an
  ask-for-everything client still pages. CITE ets-wfs20 `getfeature` remains
  the replay corpus for any follow-up.

## 3. Follow-ups (filed)

- T-K WFS read-path parity: GML output (or honest `ExceptionReport` + caps
  honesty), `srsName`, `startIndex` paging, `sortBy`, `filter`/CQL honesty,
  `GetPropertyValue`/stored-query rejects-by-name, `numberMatched` envelope.
- T-L WFS proof corpus: QGIS/GDAL replay fixtures (page-through loop against a
  >MaxFeatures layer terminating with exact totals, mirroring the T-015
  `queryAllFeatures` closeout for FeatureServer).
