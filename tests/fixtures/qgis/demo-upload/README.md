# demo-upload: QGIS upload/link demo project (T-t3x)

Five realistic layers exercising both QGIS plugin flows
(`clients/qgis/README.md`): the 3 file layers upload as copies, the 2
database layers link in place with zero bytes moved.

| Layer            | Source            | SRS        | Style                          | Plugin flow  |
|------------------|-------------------|------------|--------------------------------|--------------|
| world_countries  | demo-upload.gpkg  | EPSG:4326  | single green fill              | upload copy  |
| world_rivers     | demo-upload.gpkg  | EPSG:3857  | single blue line               | upload copy  |
| world_places     | demo-upload.gpkg  | EPSG:3395  | graduated by POP_MAX (5 class) | upload copy  |
| world_lakes      | PostGIS (compose) | EPSG:4269  | single blue fill               | link existing|
| us_states        | PostGIS (compose) | EPSG:5070  | categorized by STUSPS (56)     | link existing|

Project CRS is EPSG:3857. Everything builds offline from frozen
snapshots in `sources/` (`sources/attribution.txt`: Natural Earth
110m v5.1.1 + Census 2018 5m states).

## Rebuild (offline, idempotent)

Requires GDAL (`ogr2ogr`); the `qgz` step additionally needs QGIS
with Python bindings (`import qgis.core`, QGIS 4.2.2 used here).

```sh
cd tests/fixtures/qgis/demo-upload
python3 scripts/build.py              # all steps
python3 scripts/build.py --step gpkg  # demo-upload.gpkg only
python3 scripts/build.py --step postgis  # postgis/init.sql only
python3 scripts/build.py --step qgz   # demo-upload.qgz only
```

## Run the demo

1. Seed PostGIS:
   ```sh
   cd tests/fixtures/qgis/demo-upload/postgis
   docker compose up -d
   docker compose exec db psql -U demo -d demo -c \
     "SELECT 'world_lakes', COUNT(*) FROM world_lakes UNION ALL SELECT 'us_states', COUNT(*) FROM us_states;"
   # expect 24 lakes + 56 states
   ```
2. Open `demo-upload.qgz` in desktop QGIS. With compose down the
   project still opens: the 3 GPKG layers paint and the 2 PostGIS
   layers report unavailable (expected — start compose and reopen).
   With compose up all 5 layers paint.
3. Plugin upload (`clients/qgis/` dock plugin against a live host):
   - Upload copy: select a GPKG layer (countries, rivers or places)
     → export → staged `POST /api/uploads` + `PUT` chunks →
     `POST /api/ingest` → append to map.
   - Link existing: pick the PostGIS store in `GET /api/catalogue`,
     then `GET` + `PUT /api/maps/{name}` referencing
     `public.world_lakes` / `public.us_states` — zero bytes move.
4. Confirm on the engine: `GET /api/maps` shows the map with all 5
   datasets servable (FeatureServer, MapServer, Tiles, Wms, Wfs).

## Teardown

```sh
cd tests/fixtures/qgis/demo-upload/postgis
docker compose down -v
```
