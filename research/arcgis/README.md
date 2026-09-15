# ArcGIS REST endpoint autoresearch

> Conformance catalogue (T-012, checked 2026-09-13):
> [`conformance-sources.md`](conformance-sources.md) — external test corpora
> (arcgis-rest-js, Esri reference, Koop, pygeoapi, GDAL) and 15 implementable
> serve/consume tests with request, expected assertion, real-client
> dependency, current-behaviour pointer and effort.

Ground-truth corpus for the GeoServices work (ADR-0035):

- **consume:** `Spatial.Stores.ArcGisRest` (the engine reads ArcGIS REST), and
- **codec:** `Spatial.Esri.Codec` (the shape of Esri JSON itself).

The suite in `tests/unit/Spatial.Stores.ArcGisRest.Tests/RealWorldFixtureTests.cs`
replays responses recorded from **real, public ArcGIS services** and checks the
provider against them. The corpus is how we know the provider speaks the dialect
real servers speak, not just the shapes we happened to hand-write.

## The loop

```text
seeds.txt ──crawl──► catalogs ──discover──► Feature/MapServers ──probe──► responses
    ▲                                                                       │
    │                                                                     slim
    └──────────── gap list ◄──── report.py ◄──── index.json ◄──── captured/ ┘
                                     ▲
                        discover_agol.py (targeted gap filling)
```

1. **Discover.** `harvest.py` crawls the catalog roots in `seeds.txt` (folders,
   recursively) and collects `FeatureServer`/`MapServer` services. When the
   crawl under-covers a dimension, `discover_agol.py` queries the ArcGIS Online
   search API for popular public Feature Services and writes
   `extra-services.txt`, which `harvest.py --services-file` probes directly.
2. **Probe.** For every layer the harvester records the service root, layer
   metadata, a bounded `query` page (`where=1=1`, `outFields=*`,
   `resultRecordCount=5`), `returnCountOnly`, `returnIdsOnly`, and a deliberate
   bad-`where` error. Responses are cached under `cache/` (gitignored) so a run
   is idempotent and re-recording never re-hits the network.
3. **Record.** Each envelope is written to
   `tests/fixtures/arcgis/captured/<slug>/` with a per-service `manifest.json`;
   `index.json` is rebuilt from those manifests, so partial and targeted runs
   compose instead of overwriting each other.
4. **Slim.** `slim.py` trims process output (contour layers can be tens of MB)
   to the field definitions, identities, attributes and geometry categories the
   provider actually reads. Idempotent.
5. **Report.** `report.py` measures the corpus against the dimensions the
   provider branches on and prints the gaps that the next iteration should
   close. The gaps are the shopping list; the loop ends when the gap list is
   empty or the remaining gaps are documented.

Run it:

```bash
python3 research/arcgis/harvest.py --max-services 40 --max-per-catalog 12 --depth 2
python3 research/arcgis/discover_agol.py --max 40
python3 research/arcgis/harvest.py --services-file research/arcgis/extra-services.txt
python3 research/arcgis/slim.py
python3 research/arcgis/report.py
```

## Fixture refresh (T-065): nightly/explicit, PR gate stays offline

Last refresh check: **2026-09-14** (`eng/refresh-esri-fixtures.sh --check`:
`geometry-project` SAME, `basemap-root` SAME, `geometry-root` DRIFT — the
live sampleserver6 GeometryServer root now answers only `serviceDescription`,
without the `currentVersion`/`capabilities` keys the checked-in
`esriResponse` records; filed as a refresh follow-up, fixtures untouched).

Live source URLs probed on every refresh:

- GeometryServer root + project probe:
  `https://sampleserver6.arcgisonline.com/arcgis/rest/services/Utilities/Geometry/GeometryServer`
  (the esri-docs fixtures cite the shortened `sampleserver6/Geometry/
  GeometryServer` pattern plus the Esri REST docs,
  `https://developers.arcgis.com/rest/services-reference/enterprise/`) —
  diffed against `tests/fixtures/esri-docs/geometryserver/metadata.json`.
- Canvas basemap root:
  `https://services.arcgisonline.com/arcgis/rest/services/Canvas/World_Dark_Gray_Base/MapServer`
  — diffed against the recorded envelope in
  `tests/fixtures/arcgis/captured/canvas-world-dark-gray-base-mapserver/service.json`.
- Full catalog roots for the corpus pass: `seeds.txt` in this directory
  (sampleserver6/2/5, `services.arcgis.com`, NPS, USFS, NOAA, National Map,
  `services.arcgisonline.com`).

Policy:

- `eng/refresh-esri-fixtures.sh --check` (default) fetches the probes,
  prints the SAME/DRIFT/UNREACHABLE summary, and changes nothing under
  `tests/`. `--write` refreshes the mapped envelopes (esri-docs
  `esriResponse` blocks; captured corpus via `harvest.py --services-file` +
  `slim.py`) and prints `git diff --stat` for a refresh PR.
- The default suite (`EsriDocsReplayTests`, `RealWorldFixtureTests`)
  replays checked-in fixtures only — no network. Live probes live in
  `EsriLiveRefreshTests` and are skipped unless `SPATIAL_ESRI_LIVE=1`.
- `eng/verify.sh` never invokes the refresh script (pinned by
  `EsriRefreshGateTests`). Drift a refresh finds is filed via
  `eng/tasks add --area interop.esri`, never fixed by editing expectations
  or product code in the refresh itself.

## Current corpus

52 endpoints / 316 layers across 9 portal catalogs
(ArcGIS Online orgs, NOAA, NPS, US Forest Service, USGS/National Map, Esri
sample servers).

| Dimension | Covered |
| --- | --- |
| Service types | 38 `FeatureServer`, 14 `MapServer` |
| Geometry | point (73), polygon (138), multipoint (42), polyline (5), non-spatial/group (58) |
| Field types | OID, SmallInteger, Integer, Single, Double, String, Date, GlobalID, Geometry |
| Spatial refs | 4326, 3857/102100, 4269, 32633, 6318/104145, 3059, 3087, 102440 |
| Behaviours | 230 paged (`exceededTransferLimit`), 54 empty, 21 null-geometry, 316 error probes |
| Errors | `400` bad query, `499` token-required (service root) |

### Known gaps (next iterations)

- `esriGeometryEnvelope` is not produced by the sampled services. The codec
  decodes envelopes (as query inputs, `EsriGeometryCodecTests`) — no real
  service declares an envelope *layer*, so this is a corpus limit, not a code
  gap.
- `esriFieldTypeGUID` is not represented (`GlobalID` is). Both map to
  `AttributeKind.Guid` and are pinned by `EsriFieldTypeTests` /
  `EsriAttributeCodecTests`, so only the corpus dimension is missing.
- SRIDs outside the curated `WkidMap` (3059, 3087, 6318, 102440) decode with no
  CRS by design; the map is a deliberate allow-list (ADR-0035).
- The 58 non-spatial layers are resolved: 35 **group layers** are now skipped by
  the provider (containers, not queryable data; see
  `A_group_layer_is_not_listed_as_a_dataset`) and 23 **tables** are listed as
  non-spatial datasets (`A_table_layer_is_listed_but_has_no_geometry`).

## What the recorded corpus found

The fixture sweep failed on first run and surfaced three real defects, all
fixed here:

1. **Geometry column mismatch.** `ArcGisRestMapper.Schema` kept the Esri field
   name (`Shape`/`SHAPE`) but the dataset advertised the canonical
   `geometry` column, so `EsriFeatureCodec.Decode` never found the geometry and
   threw on the geometry-kind field. The mapper now drops the Esri geometry
   field and appends the canonical `geometry` column.
2. **Missing `objectIdField`.** Older/MapServer layers omit `objectIdField`;
   the provider defaulted to `OBJECTID` and failed. It now derives the identity
   from the single `esriFieldTypeOID` field.
3. **Case-sensitive attributes.** MapServer can declare `OBJECTID` and emit
   `objectid`. Esri field names are case-insensitive, so `EsriAttributeCodec`
   and `EsriFeatureCodec` now match attributes case-insensitively.

## Tests

`RealWorldFixtureTests`:

- `Recorded_layer_decodes_exactly_as_the_service_returned_it` — a theory over
  every captured spatial layer: replays the recorded service root / metadata /
  query through `ArcGisRestStore`, then checks the decoded schema, field-type
  mapping, geometry field, SRID, feature identities, geometry presence and
  attribute values against the recorded Esri JSON.
- `List_returns_every_layer_of_a_real_service` — service-root → datasets.
- `A_table_layer_is_listed_but_has_no_geometry` and
  `A_wkid_outside_the_curated_map_leaves_geometry_without_a_crs` — characterise
  the documented gaps so a behaviour change is a test failure, not a surprise.
- `A_token_required_error_maps_to_store_unavailable` — ArcGIS `499`.

The fixtures are copied into the test output as `arcgis-fixtures/` by
`Spatial.Stores.ArcGisRest.Tests.csproj`. To add a corpus, refresh the
fixtures and re-run `dotnet test`; no test code changes are needed for new
layers.
