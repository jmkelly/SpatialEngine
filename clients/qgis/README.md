# QGIS plugin — Spatial Engine uploader/linker

Thin QGIS 3.34+ dock client over the neutral REST surface (no Esri projection):

- **Upload copy**: export active vector layer → GeoJSON → staged
  `POST /api/uploads` + `PUT` chunks (resumable past ~10 MB) →
  `POST /api/ingest?dataset=&srid=[&sourceSrid=][&publish=]` → append to map.
- **Link existing**: `GET /api/catalogue?store=postgis|sqlserver` picker, then
  `GET` + `PUT /api/maps/{name}` referencing `schema.table` — zero bytes move.
- **Connection push**: explicit admin-only `PUT /api/connections/{store}`
  pre-filled from the QGIS layer URI (blind overwrite, secret never read back).
- **Styles**: QML subset → MapLibre fragments (`style_convert.py`); complex
  renderers fall back to engine default with a warning.
- **Services**: appends union `feature, map, tiles, wms, wfs`
  (the host's wire keys for Feature/Map/Tiles/Wms/Wfs;
  `image` needs an image layer — out of v1 vector scope).
- **Picker**: only servable vector layers are listed (`layer_filter.py`).

Pure modules (`engine_client`, `map_builder`, `layer_filter`,
`style_convert`) import no QGIS code: `python3 -m pytest clients/qgis/tests`.
