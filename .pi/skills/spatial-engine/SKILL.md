---
name: spatial-engine
description: Operate the Spatial Engine as a client, through the Spatial CLI and its stores. Use when asked to add, list, describe, style, publish or remove spatial datasets and maps; when running the engine end to end (start host, health, ingest, MapServer); when touching clients/dotnet/Spatial.Cli, a spatial.json project file, store selection (demo, memory, postgis), or a SpatialException exit code. For repo-wide quality gates use the quality-loop skill; to decide where a code change belongs read architecture/distilled/README.md.
---

# Spatial Engine

Drive the engine through its public surface. The CLI (ADR-0052) is a pure
client of the host HTTP API: it holds no spatial algorithm and calls no
provider directly. **Drive it, don't reach in** - when a CLI verb exists, do
not touch the store, the provider or the HTTP route underneath it.

Two facts shape every session:

- The CLI is a client, so it needs a **running host** and, for every mutation, an **admin token**.
- Every failure is a structured `SpatialException` code that becomes the **process exit code**. Branch on the code, never on the message text.

## 0. Start a host

```bash
SPATIAL_ADMIN_TOKEN=dev-token \
  dotnet run --project src/Spatial.Host --urls http://127.0.0.1:5201 &
curl -s http://127.0.0.1:5201/health/ready
# {"status":"ready","stores":["demo","memory","postgis"]}
```

`demo` and `memory` are always available; `postgis` appears only when a
connection is configured. Writes default to `memory`.

Define the client once for the session:

```bash
CLI="dotnet run --project clients/dotnet/Spatial.Cli -- --host http://127.0.0.1:5201"
export SPATIAL_ADMIN_TOKEN=dev-token
```

Need real data? `eng/seed.sh` fetches public data, ingests it and publishes
styled services, leaving a host on `http://127.0.0.1:5201`.

## 1. Add

`--dry-run` validates and plans without mutating, so run it first whenever the
file or the flags are new.

```bash
$CLI dataset add --file /abs/path/reference.geojson --dataset public.reference \
  --srid 4326 --token "$SPATIAL_ADMIN_TOKEN" --dry-run
$CLI dataset add --file /abs/path/reference.geojson --dataset public.reference \
  --srid 4326 --token "$SPATIAL_ADMIN_TOKEN"
# public.reference: 4 feature(s) loaded into memory
```

Exactly one of `--file` or `--url`. Also `--format geojson|ndjson|csv`,
`--identity none|auto|source` (with `--identity-field`), `--source-srid` to
have the engine reproject (ADR-0041), and `--publish NAME` to register a
feature map in the same call.

## 2. Query

```bash
$CLI dataset list --store memory --pattern 'public.%'
$CLI dataset describe public.reference --store memory
```

`describe` reports geometry type, SRID, row estimate, identity field and the
field list. A dataset that mixes geometry families reports the type of its
**first feature**, so read the rows before trusting the header.

A published map is queryable over Esri GeoServices REST, the interop surface:

```bash
curl -s "http://127.0.0.1:5201/arcgis/rest/services/Reference/MapServer/0/query?\
where=kind%3D%27city%27&outFields=name&returnCountOnly=true&f=json"
```

Layer `0` is the map's first layer; pass `f=json` to make the response
format explicit rather than relying on the service default.

## 3. Compose and style a map

`--kind` picks the projection: `feature` becomes a FeatureServer, `map` a
MapServer, `image` an ImageServer (ADR-0035/0048/0051).

```bash
$CLI map create --name Reference --kind map --store memory \
  --layer public.reference=Features --token "$SPATIAL_ADMIN_TOKEN"
$CLI map set-style --map Reference --dataset public.reference \
  --geometry mixed --color '#4fc3f7' --opacity 0.85 --token "$SPATIAL_ADMIN_TOKEN"
$CLI map show Reference
$CLI map export Reference --format url
```

The draw recipe lowers to the persisted MapLibre fragment (ADR-0047):
`--geometry polygon|line|point|mixed`, plus `--color #rrggbb`, `--opacity 0..1`,
`--line-width`, `--radius`, `--hidden`. A recipe the renderer would reject
fails as `invalid.arguments` before it reaches the store.

## 4. Remove

```bash
$CLI map remove-layer --map Reference --dataset public.reference --token "$SPATIAL_ADMIN_TOKEN"
$CLI map delete Reference --token "$SPATIAL_ADMIN_TOKEN"
$CLI map show Reference   # error: not.found ... ; exit 3
```

## Exit codes are the contract

| Code | `SpatialException` | Meaning |
| --- | --- | --- |
| 0 | none | success |
| 2 | `invalid.arguments` | bad input, missing token, unparseable style, existing map without `--force` |
| 3 | `not.found` | no such map or dataset |
| 4 | `store.unavailable` | host down or connection refused |
| 5 | none | cancelled |
| 1 | none | unexpected, or a host error with no mapped code |

## Things that surprise you

- **`--file` resolves against the process working directory**, not the project file. Pass an absolute path when the working directory is uncertain.
- **A map must keep at least one layer.** `map remove-layer` on the only layer fails with `invalid.arguments`; delete the map to empty it.
- **`map add-layer` refuses a dataset the map already exposes.** Remove the layer first, or replace the map with `map create --force`.
- **`map create` on an existing name fails** unless `--force` is passed; it is create-or-replace, not upsert.
- **A missing token is exit 2, not 401.** Supply `--token` or `SPATIAL_ADMIN_TOKEN`; the token never appears in output, errors or the project file.
- **`--json` is the scripting surface**: a stable `{"ok":…,"command":…,"data":…}` envelope, or `{"ok":false,"error":{"code":…}}`.
- **`project export` flattens every store's maps** into one file, `demo` included.

## What the CLI cannot do

`dataset` is `list`, `describe`, `add` and nothing else: there is no dataset
remove and no dataset update. Data goes in and comes back out through the CLI;
only maps and layers are removable. A task that needs dataset deletion is a
**host API change**, so route it through the ADRs in `architecture/decisions/`
before writing any client code.

## Reference

- `architecture/distilled/cli.md` - canonical command, option and project-file reference
- `clients/dotnet/Spatial.Cli/README.md` - the client itself
- `architecture/references/geoservices-compatibility.md` - the REST surface
- `architecture/distilled/core.md`, `plugins.md`, `rendering.md` - geometry values, implementations, tiles and labels

## Changing the engine, not just driving it

```bash
.pi/skills/spatial-engine/scripts/doctor.sh   # toolchain, working tree, task routing
```

Route the task through `architecture/distilled/README.md` (ADRs win on
conflict), land contract + SDK + test + ADR together, and gate on
`eng/verify.sh`. After a CLI or HTTP change run `eng/cli-e2e.sh`; after a
browser-facing change run `eng/e2e-web.sh` or `eng/workbench-e2e.sh`. Quality
gates are part of done: the quality-loop skill, branch floor in
`coverage-policy.json`.
