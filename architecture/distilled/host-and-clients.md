# Host, HTTP API, Frontend, Deployment, Security (distilled)

Implements ADR-0033 (replaces ADR-0030/0031 HTTP/workbench surfaces).

## Host API (`Spatial.Host`, ASP.NET Core minimal API, JIT)

- One JSON contract: camelCase properties and enum names; options come from
  the SDK's shared `HostApiJson`. Shapes live in `Spatial.PluginSdk.Http`;
  OpenAPI at `/openapi/v1.json` (source of the generated TypeScript wire types).
- Geometries cross as Base64 SGEOM strings, batches as Base64 SFBAT strings
  (ADR-0020). Feature data never crosses as JSON geometry.
- **Service outcomes are typed results.** `invalid.arguments` → 400,
  `not.found` → 404, `store.unavailable` → 503 (all as `ErrorResponse`
  `{code, message}`); cancelled calls → 499.
- Long work is a cancellable request, never a parked job: `CancellationToken`
  flows from the aborted connection (the demo sleep cancels over HTTP).

```
GET    /                                # identity doc (index.html when workbench served)
GET    /health/live | /health/ready     # ready includes the configured stores
POST   /api/geometry/buffer             # {geometry, distance, quadrantSegments?} -> {geometry}
POST   /api/geometry/intersection       # {left, right} -> {geometry}
POST   /api/geometry/validate           # {geometry} -> {valid}
POST   /api/geometry/simplify           # {geometry, tolerance} -> {geometry}
POST   /api/crs/describe                # {crs} -> CrsDescription
POST   /api/coordinates/transform       # {geometry, source?, target} -> {geometry}
GET    /api/catalogue?store=&pattern=   # DatasetSummary[]
GET    /api/datasets/{id}?store=        # DatasetDescription
POST   /api/datasets?store=             # {dataset, batch, srid} -> {dataset}
POST   /api/features/scan?store=        # {dataset} -> {batches[]}
POST   /api/features/query?store=       # {dataset, bbox?, filter?} -> {batches[]}
POST   /api/features/write?store=       # {dataset, batch, transaction?} -> {appended}
POST   /api/transactions/begin?store=   # -> {transaction}
POST   /api/transactions/commit?store=  # {transaction} -> {ok}
POST   /api/transactions/rollback?store=# {transaction} -> {ok}
POST   /api/demo/sleep                  # {milliseconds} -> {slept}
GET    /openapi/v1.json
```

The `store` query selects `demo` (default, always available) or `postgis`
(needs configuration).

## Configuration

| Key | Meaning |
| --- | --- |
| `Spatial:Postgis:ConnectionString` | PostGIS connection string (empty = unconfigured; every PostGIS call throws `store.unavailable`) |
| `SPATIAL_POSTGIS_CONNECTION` | Env fallback for the connection string — the **only** secret channel |
| `Spatial:WebRoot` | Built workbench directory; when set, `GET /` serves it |

## Clients

- TypeScript SDK: `clients/typescript` (`@spatial/client`) — one method per
  route, wire types generated from OpenAPI (`scripts/generate.mjs`),
  drift-checked in `npm test`, includes the SFBAT decoder.
- .NET SDK: `clients/dotnet/Spatial.Client` — one typed method per route,
  core geometry values in and out, `SpatialClientException` failures.
- `eng/e2e-web.sh` proves the real host end-to-end from the TS SDK.

## Frontend boundary

- Workbench = React 19 + TypeScript + MapLibre; talks **only** to the public
  host API through the TS SDK; engine-neutral app state; no spatial logic.
  Only client-side spatial code: `src/sgeom.ts` (SGEOM → GeoJSON, byte-exact
  codec mirror) and `src/click-match.ts` (pure projection math, no pixel reads).
- Must run in a normal browser with zero Tauri dependency — Playwright
  (`eng/workbench-e2e.sh`); `package.json` must never include `@tauri-apps/*`
  (architecture test).
- Persistence: results/recent runs in localStorage; geometry stored as the
  host-produced SGEOM base64, never re-encoded client-side.
- MapLibre worker pinned: `public/maplibre-gl-worker.mjs` via `setWorkerUrl`.
- Tauri shell: packages React assets, window/lifecycle/sidecar/narrow
  adapters. **No geometry, operations, provider logic or project-domain
  behaviour in the shell.** Desktop conformance mirrors browser conformance.

## Deployment profiles

| Profile | Shape |
| --- | --- |
| Browser/server | React assets + ASP.NET Core host + external PostGIS; services in-process |
| Local development | PostGIS container; hot reload; demo store for Docker-free work |
| Desktop | Tauri 2 + React production assets + self-contained host sidecar or configured remote host |

Invariants: one public API/contract set/TS SDK/React app in every profile;
host independently executable; browser tests never require Tauri.

## Security model

- Implementation projects are fully trusted in-process code.
- **Secrets flow host config → options only**
  (`PostgisOptions.ConnectionString`). Request bodies never carry
  connection material.
- **Redaction is a store diagnostic contract**: no secret in logs or
  `SpatialException` messages; unconfigured store fails with actionable
  `store.unavailable` naming the setting; failures describe config in
  redacted form (db name only) — asserted by a redaction test.
- Client-supplied text never becomes SQL structure: strict identifier
  grammar + bound parameters (see `contracts.md`).
- The demo store is read-only; writes/creation/transactions against it are
  `invalid.arguments`.
- Payload and time limits enforced where supported; diagnostics are structured.
