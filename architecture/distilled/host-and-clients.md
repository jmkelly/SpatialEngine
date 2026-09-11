# Host, HTTP API, Frontend, Deployment, Security (distilled)

Implements ADR-0014/0015/0016/0017/0018/0019/0030/0031.

## Host API (`Spatial.Host`, ASP.NET Core minimal API, JIT)

- One JSON contract: camelCase properties and enum names; options come from
  the SDK's shared `HostApiJson`. Shapes live in `Spatial.PluginSdk.Http`;
  OpenAPI at `/openapi/v1.json` (source of the generated TypeScript wire types).
- Inline values use `Spatial.PluginSdk.Codec.ValueCodec` — the same codec as
  the worker wire (`$i64`, `$bytes`, `$geometry`, `$crs`, `$resource`).
  Feature data never crosses inline — bounded streams only.
- **Runtime outcomes are always 2xx** (`completed` with `ok: true/false`).
  4xx/5xx reserved for undecodable requests and host failures.
- Long work is a job, never a parked request: long-running capabilities (or
  `wait: false`) return `202` + job to poll/subscribe.

```
GET    /                                # identity doc (index.html when workbench served)
GET    /health/live | /health/ready
GET    /api/capabilities[/{id}]
POST   /api/invocations                 # capability, arguments, permissions, deadline, provider?, resource?, wait?
GET    /api/jobs/{id}                   # snapshot: state, provider, step, error code
POST   /api/jobs/{id}/cancel
GET    /api/jobs/{id}/events            # JSON page; Accept: text/event-stream replays + pushes until `event: done`
GET    /api/resources/{id}/metadata
DELETE /api/resources/{id}              # dispose (closes stream, releases leases)
GET    /api/resources/{id}/stream       # NDJSON of codec-encoded items; final {"$error":…} line on failure
GET    /api/plugins[/{id}]
POST   /api/plugins/{id}/route-new-work | /drain | /rollback   # ADR-0031; 404 no supervisor/unknown id; 409 bad state
GET    /openapi/v1.json
```

- Stream reads: consuming to end closes the resource; abandoning the read also
  closes it (backpressured producers always released).
- Plugin control endpoints map 1:1 to supervisor primitives (`RouteNewWorkTo`,
  `DrainAsync`, `RollbackAsync`); responses are the updated `PluginDto`.

## Configuration

| Key | Meaning |
| --- | --- |
| `Spatial:PackagesRoot` | immutable plugin packages to activate (empty = none) |
| `Spatial:Preferences` | configured provider preferences (`{"capability@1":"provider@1"}`) |
| `Spatial:WorkerEnvironment` | host-managed worker env vars — the **only** secret channel |
| `Spatial:WebRoot` | built workbench directory; when set, `GET /` serves it |

The host waits for workers at startup (readiness); a failed activation is
reported on `/api/plugins` and never blocks the rest.

## Clients

- TypeScript SDK: `clients/typescript` (`@spatial/client`) — wire types
  generated from OpenAPI (`scripts/generate.mjs`), drift-checked in `npm test`,
  includes the SFBAT decoder.
- .NET SDK: `clients/dotnet/Spatial.Client` — typed methods, shared codec,
  `ReadFeatureBatchesAsync`, `WaitForJobAsync`.
- `eng/e2e-web.sh` proves the real host end-to-end from the TS SDK.

## Frontend boundary

- Workbench = React 19 + TypeScript + MapLibre; talks **only** to the public
  host API through the TS SDK; engine-neutral app state; no spatial logic.
  Only client-side spatial code: `src/sgeom.ts` (SGEOM → GeoJSON, byte-exact
  codec mirror) and `src/click-match.ts` (pure projection math, no pixel reads).
- Must run in a normal browser with zero Tauri dependency — Playwright
  (`eng/workbench-e2e.sh`); `package.json` must never include `@tauri-apps/*`
  (architecture test).
- Persistence: results/recent jobs in localStorage; geometry stored as the
  host-produced SGEOM base64, never re-encoded client-side.
- MapLibre worker pinned: `public/maplibre-gl-worker.mjs` via `setWorkerUrl`.
- Tauri shell: packages React assets, window/lifecycle/sidecar/narrow
  adapters. **No geometry, operations, provider logic, capability resolution
  or project-domain behaviour in the shell.** Desktop conformance mirrors
  browser conformance.

## Deployment profiles

| Profile | Shape |
| --- | --- |
| Browser/server | React assets + ASP.NET Core host + external PostGIS; plugins out-of-process |
| Local development | .NET Aspire composition; PostGIS container; hot reload; workers out-of-process |
| Desktop | Tauri 2 + React production assets + self-contained host sidecar or configured remote host |

Invariants: one public API/contract set/TS SDK/React app in every profile;
host independently executable; every profile runs the same conformance tests;
browser tests never require Tauri.

## Security model

- Plugins receive no ambient authority; third-party plugins run out of
  process (or WASM).
- **Secrets flow host config → worker launch environment only**
  (`WorkerSupervisorOptions.WorkerEnvironment`, e.g.
  `SPATIAL_POSTGIS_CONNECTION`). Invocations never carry connection material.
- **Redaction is a provider diagnostic contract**: no secret in logs,
  `CapabilityError` messages, or the wire; unconfigured provider fails with
  actionable `provider.unavailable` naming the env var; failures describe
  config in redacted form (db/schema only) — asserted by a redaction test.
- Client-supplied text never becomes SQL structure: strict identifier
  grammar + bound parameters (see `contracts.md`).
- Permissions are caller-asserted scoping, not authorization: the caller
  declares `permissions`, the host forwards them unchanged, and the runtime
  permission gate enforces them against the capability's declared
  requirements. A host-side grant policy is not implemented yet; the loopback
  bind (`Host` listens on loopback locally) is the trust boundary, so remote
  deployment must add its own authentication/policy in front.
- Payload, stream, memory and time limits enforced where supported; audit
  events and provenance are structured.
