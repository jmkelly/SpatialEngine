# Spatial CLI (distilled)

`clients/dotnet/Spatial.Cli` is a dependency-free console client of the
public host HTTP API (ADR-0052). It adds datasets, composes maps with
layers and styles, stores the workspace as a declarative project file, and
reports the GeoServices endpoints each map projects to. It holds no
spatial algorithm and calls no provider directly.

## Running

```bash
dotnet run --project clients/dotnet/Spatial.Cli -- --host http://127.0.0.1:5201 dataset list
dotnet run --project clients/dotnet/Spatial.Cli -- --help

# one self-contained binary
dotnet publish clients/dotnet/Spatial.Cli -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist/spatial
```

Global options (valid before or after the command):

| Option | Env | Meaning |
| --- | --- | --- |
| `--host <url>` | `SPATIAL_HOST` | Host base address (default `http://127.0.0.1:5201`) |
| `--token <token>` | `SPATIAL_ADMIN_TOKEN` | Admin token for mutations; never echoed |
| `--store <name>` | — | Default store (`demo`, `memory`, `postgis`); writes default `memory` |
| `--project <path>` | — | Project file (default `spatial.json`) |
| `--json` | — | Machine-readable `{ok, command, data}` output |
| `--quiet` / `--verbose` | — | Less / more human output |
| `--dry-run` | — | Validate and print the plan without mutating the host |
| `--timeout <seconds>` | — | Per-request timeout (default 100) |
| `--help` | — | Command help, rendered from the catalog |

Exit codes: `0` success, `2` invalid arguments, `3` not found,
`4` store/host unavailable, `5` cancelled, `1` unexpected.

## Commands

| Group | Verb | Purpose |
| --- | --- | --- |
| `host` | `health` | Read `GET /health/ready` and report reachability + stores |
| `dataset` | `list` | `GET /api/catalogue` for a store (`--pattern` filters) |
| `dataset` | `describe <dataset>` | `GET /api/datasets/{id}` (fields, geometry, identity) |
| `dataset` | `add` | `POST /api/ingest` a local file (`--file`) or URL (`--url`) |
| `map` | `list` | List publications |
| `map` | `show <name>` | One publication with layers + parsed styles |
| `map` | `create` | Create/replace a publication (`--kind feature|map|image`) |
| `map` | `delete <name>` | Delete a runtime publication |
| `map` | `layer add` / `layer remove` | Add/remove a layer, preserving stable ids |
| `map` | `style set` | Set one layer's compact draw recipe |
| `map` | `export <name>` | Publication + GeoServices endpoint(s) |
| `project` | `init` | Write a starter project file |
| `project` | `plan` | Dry-run an apply; print what would change |
| `project` | `apply` | Ingest datasets + publish maps from the file (idempotent) |
| `project` | `export` | Serialise the host's datasets + publications to the file |

### `dataset add`

```
--file <path>            local file to upload          (or --url <https://…>)
--dataset <schema.table> destination dataset id (required)
--srid <int>             CRS of the stored geometry (required)
--format <geojson|ndjson|csv>   default geojson
--identity <none|auto|source>   default auto
--identity-field <field> identity column when --identity source
--source-srid <int>      CRS of the file; engine reprojects (ADR-0041)
--publish <name>         also register a feature publication
```

## Project file (`spatial.json`)

```json
{
  "version": 1,
  "datasets": [
    {
      "dataset": "public.world_places",
      "srid": 4326,
      "source": "https://…/ne_110m_populated_places.geojson",
      "format": "geojson",
      "identity": "none",
      "sourceSrid": null
    }
  ],
  "maps": [
    {
      "name": "WorldReference",
      "kind": "map",
      "store": "memory",
      "description": "A styled reference map",
      "copyright": "Natural Earth",
      "layers": [
        {
          "dataset": "public.world_places",
          "name": "Places",
          "geometry": "point",
          "style": { "color": "#ffd54f", "opacity": 0.9, "radius": 3, "visible": true }
        }
      ]
    }
  ]
}
```

- `version` must be `1`; an unknown version is `invalid.arguments`.
- `source` is an `http(s)` URL or a filesystem path relative to the
  project file.
- `kind` maps to `PublicationKind`: `feature` → FeatureServer,
  `map` → MapServer, `image` → ImageServer.
- The compact `style` recipe lowers to the persisted MapLibre fragment
  (ADR-0047): a `polygon` layer draws fill + line, a `line` layer line, a
  `point` layer circle, and `mixed` draws all three. `color` defaults
  `#4fc3f7`, `opacity` `1`, `lineWidth` `2`, `radius` `5`, `visible`
  `true`.
- `apply` is idempotent: an existing dataset is reused unless `--force`;
  an existing publication keeps its stable layer ids (ADR-0041) and its
  layers are replaced by the file's ordered list.
