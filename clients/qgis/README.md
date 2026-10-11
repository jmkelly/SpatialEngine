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

## Dock UI (`dock_widget.py`, QGIS 3.34+/4.x)

One dock, two modes. **Upload copy**: pick a layer (Refresh lists only
servable vectors; hidden ones are named with reasons in the log), set map +
dataset + store + source SRID (0 = layer CRS), Upload — export runs on the
GUI thread, staged ingest + map save run on a worker with progress and
Cancel (staging is cleaned up on cancel). **Link existing**: pick a store,
Load the catalogue, pick a table, Link — zero bytes move. Connection +
token + map + store persist via QSettings. The **Connection string (admin)**
group is opt-in and pushes only after an explicit overwrite confirm; the
secret is never read back.
