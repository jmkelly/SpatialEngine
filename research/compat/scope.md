# Scope matrix: catalog, admin, and the servers we don't run

Sources (checked 2026-09-14):
- S1 Catalog: https://developers.arcgis.com/rest/services-reference/enterprise/catalog/
- S2 Geocode Service: https://developers.arcgis.com/rest/services-reference/enterprise/geocode-service/
  (ops: `findAddressCandidates`, `reverseGeocode`, `geocodeAddresses` batch)
- S3 GP Service: `.../enterprise/geoprocessing-service/` (+ `gp-service-*` job/result pages)
- S4 Services-directory default: `f=html` is the ArcGIS default (`query-fs` §1.2:
  "`f` **default is `html`**"); `f=pjson` used in examples
- S5 Network Analysis / GeoEvent / Stream / Knowledge / Workflow / Data Store /
  Server admin roots (enterprise admin API family)
- G1 Ground truth: `ground-truth/catalog.sampleserver6.json`
  (`currentVersion`, `folders[]`, `services[{name,type}]` incl. `GPServer` entries)

Our surface: `GeoServicesCatalog.cs`, `EsriAdminEndpoints.cs`
(`/arcgis/admin/...`, token-gated), neutral `/api/maps` + `/api/ingest`
(ADR-0041/0053), `EsriJson.cs` (`EsriFormat.Ensure`).

## 1. Capabilities

| Capability | Ours | Status | Evidence |
|---|---|---|---|
| Services catalog (`GET /arcgis/rest/services`, folders, `services[{name,type}]` per enabled map service) | served | **Have** | `GeoServicesCatalog.cs`; `GeoServicesEndpoints.cs`; replay G1 |
| `f=pjson` alias on catalog/roots | served | **Have** | `EsriJson.cs` (GDAL/pygeoapi proof) |
| `f=html` Services Directory (human-browsable) | honestly rejected | **Non-goal** | typed `invalid.arguments` naming JSON (§7.1); facade is a JSON API by design (ADR-0035) |
| Token/auth (`generateToken`, 498/499, `token=` param) | — | **Non-goal** | static admin token only (`Spatial:Admin:Token`); provider maps 498/499→`store.unavailable` by design (§7.1) |
| Esri admin projection (`createService/deleteService/uploads/publish`) | served (gated) | **Have** | `EsriAdminEndpoints.cs`; neutral `/api/maps`+`/api/ingest` underneath |
| Geocode Server (`findAddressCandidates`, `reverseGeocode`, batch) | — | **Non-goal** | no locator/address model in engine; absent, not emulated (S2) |
| GP Server (`submitJob`, job polling, results) | — | **Non-goal** | no job model by architecture (ADR-0033); absent, not emulated (S3) |
| Network Analysis / Route / Closest Facility / Service Area | — | **Non-goal** | no network model; absent |
| GeoEvent / Stream / Knowledge Graph / Workflow Manager / Data Store admin | — | **Non-goal** | no model; absent |
| `GPServer` entries in catalog.services | — | **Partial** | G1 lists `911CallsHotspot:GPServer`; our catalog only advertises served types — correct, but worth one honesty test |

## 2. Follow-ups (filed)

- T-N Catalog honesty + directory scope: `GPServer`-style unserved-type honesty
  test, `f=html` reject message review, admin-projection parity note
  (create/delete/publish vs `.../admin` dry-run parity), and recording the
  Geocode/GP/Network non-goals in the distilled docs if they aren't already.
