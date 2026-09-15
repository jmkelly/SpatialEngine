# Spatial CLI

`clients/dotnet/Spatial.Cli` is a dependency-free console client of the
public host HTTP API. It is the CLI decided by
[ADR-0052](../../../architecture/decisions/ADR-0052-spatial-cli-is-a-public-api-client.md):
a pure client that adds datasets, composes maps with layers and styles,
stores the workspace as a declarative project file, and reports the
GeoServices endpoints each map projects to. It holds no spatial algorithm,
calls no provider directly, and takes no third-party packages, so it can be
published as one self-contained binary. The distilled command reference is
[`architecture/distilled/cli.md`](../../../architecture/distilled/cli.md).

## Running

```bash
# from the repository root
dotnet run --project clients/dotnet/Spatial.Cli -- --host http://127.0.0.1:5201 dataset list
dotnet run --project clients/dotnet/Spatial.Cli -- --help

# one self-contained binary
dotnet publish clients/dotnet/Spatial.Cli -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o dist/spatial
dist/spatial host health
```

Global options are valid before or after the command:

| Option | Env | Meaning |
| --- | --- | --- |
| `--host <url>` | `SPATIAL_HOST` | Host base address (default `http://127.0.0.1:5201`) |
| `--token <token>` | `SPATIAL_ADMIN_TOKEN` | Admin token for mutations; never echoed |
| `--store <name>` | — | Default store (`demo`, `memory`, `postgis`); writes default `memory` |
| `--project <path>` | — | Project file (default `spatial.json`) |
| `--geoservices-root <path>` | — | GeoServices route prefix (default `/arcgis/rest/services`) |
| `--json` | — | Machine-readable `{ok, command, data}` output |
| `--quiet` / `--verbose` | — | Less / more human output |
| `--dry-run` | — | Validate and print the plan without mutating the host |
| `--timeout <seconds>` | — | Per-request timeout (default 100) |
| `--help` | — | Command help, rendered from the catalog |

Mutating commands require an admin token, either `--token` or
`SPATIAL_ADMIN_TOKEN`; the token is never written to output, errors or the
project file.

## Commands

### `host`

```bash
spatial host health                              # readiness and configured stores
```

### `dataset`

```bash
spatial dataset list --pattern 'public.%'        # datasets in the store
spatial dataset describe public.world            # fields, geometry and identity
spatial dataset add --file ./world.geojson --dataset public.world --srid 4326 --token "$TOKEN"
spatial dataset add --url https://example.com/world.geojson --dataset public.world --srid 4326 \
  --source-srid 3857 --identity auto --publish World --token "$TOKEN"
```

`dataset add` uploads one local file (`--file`) or remote `http(s)` URL
(`--url`) through `POST /api/ingest`. Exactly one source is required.
`--format` is `geojson` (default), `ndjson` or `csv`; `--identity` is
`none`, `auto` (default) or `source`, where `source` requires
`--identity-field`. `--source-srid` asks the engine to reproject the file
(ADR-0041). `--dry-run` prints the plan without calling the host.

### `map`

```bash
spatial map list                                 # every map
spatial map show World                           # layers and parsed styles
spatial map create --name World --kind map --store memory --layer public.world=Countries --token "$TOKEN"
spatial map delete World --token "$TOKEN"
spatial map add-layer --map World --dataset public.cities --name Cities --token "$TOKEN"
spatial map remove-layer --map World --dataset public.cities --token "$TOKEN"
spatial map set-style --map World --dataset public.world --geometry polygon --color '#4fc3f7' --token "$TOKEN"
spatial map export World --format url            # GeoServices endpoint only
```

A "map" is the neutral `Map`; `--kind` selects the projection:
`feature` → FeatureServer, `map` → MapServer, `image` → ImageServer
(ADR-0035/0048/0051). `--layer DATASET[=NAME]` is repeatable. `set-style`
takes a compact draw recipe (`--geometry`, `--color`, `--opacity`,
`--line-width`, `--radius`, `--hidden`) that lowers to the persisted
MapLibre fragment (ADR-0047).

### `project`

```bash
spatial project init                             # write a starter spatial.json
spatial project plan                             # what apply would change
spatial project apply                            # ingest datasets + publish maps (idempotent)
spatial project export                           # serialise the host back to spatial.json
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
- `source` is an `http(s)` URL or a filesystem path relative to the project
  file.
- `kind` maps to `MapServiceKind`: `feature` → FeatureServer,
  `map` → MapServer, `image` → ImageServer.
- The compact `style` recipe lowers to the persisted MapLibre fragment
  (ADR-0047): `polygon` draws fill + line, `line` draws line, `point`
  draws a circle and `mixed` draws all three. `color` defaults `#4fc3f7`,
  `opacity` `1`, `lineWidth` `2`, `radius` `5`, `visible` `true`.
- `apply` is idempotent: an existing dataset is reused unless `--force`;
  an existing map keeps its stable layer ids (ADR-0041) and its
  layers are replaced by the file's ordered list.
- The project file never carries the admin token (ADR-0052).

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Unexpected failure or unmapped host error |
| `2` | Invalid arguments (`invalid.arguments`) |
| `3` | Not found (`not.found`) |
| `4` | Store or host unavailable (`store.unavailable`) |
| `5` | Cancelled |

Under `--json` every result is a stable envelope:
`{"ok":true,"command":"…","data":…}` or
`{"ok":false,"error":{"code":"…","message":"…"}}`.

## See also

- [`architecture/distilled/cli.md`](../../../architecture/distilled/cli.md) — the command reference
- [ADR-0052](../../../architecture/decisions/ADR-0052-spatial-cli-is-a-public-api-client.md) — the CLI as a declarative public-API client
- [`../Spatial.Client`](../Spatial.Client) — the .NET SDK the CLI builds on
