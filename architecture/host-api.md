# Host API

Read when implementing, extending or consuming the public HTTP API of the
independently executable spatial host. Part of Phase 9 (plan §16, Epic H);
implements ADR-0018 and extends ADR-0020/0022/0023/0025/0028. The API is
documented operationally here; the SDK contract shapes live in
`Spatial.PluginSdk.Http` and the OpenAPI description is served at
`/openapi/v1.json` (snapshot in `clients/typescript/scripts/`).

## Shape

- **One JSON contract**: camelCase property names; enum members as their
  camelCase names. The host configures its JSON options from the SDK's
  `HostApiJson` and every client shares that options instance.
- **Inline values use the SDK's value codec** (`Spatial.PluginSdk.Codec`,
  ADR-0030): scalars, `$i64`, `$bytes`, `$geometry` (canonical SGEOM
  interchange, ADR-0020), `$crs`, `$resource`. Feature data never crosses
  inline — it crosses as bounded streams (ADR-0023).
- **Runtime outcomes are 2xx, always**: an invocation that reached the
  runtime returns `completed` with `ok: true`/`false` and either the
  wire-encoded result or a `CapabilityErrorDto` (`capability.not.found`,
  `permission.denied`, `invalid.arguments`, …). HTTP 4xx/5xx are reserved
  for requests that cannot be decoded and host failures.
- **Long work is a job, never a parked request**: long-running capabilities
  (and any invocation with `wait: false`) start as tracked jobs and the
  response carries the job id to poll or subscribe (ADR-0008).

## Endpoints

```
GET    /                      # host identity + api/openapi locations
GET    /health/live           # process up
GET    /health/ready          # host wired, plugins counted
GET    /api/capabilities      # every registered capability + serving providers
GET    /api/capabilities/{id} # full contract declaration (404 when unserved)
POST   /api/invocations       # one invocation (the hub)
GET    /api/jobs/{id}         # job snapshot: state, provider, step, error code
POST   /api/jobs/{id}/cancel  # request cancellation
GET    /api/jobs/{id}/events  # JSON event page, or SSE with Accept: text/event-stream
GET    /api/resources/{id}/metadata
DELETE /api/resources/{id}    # dispose (closes a stream, releases leases)
GET    /api/resources/{id}/stream  # NDJSON of codec-encoded items
GET    /api/plugins           # activated worker packages and lifecycle
GET    /api/plugins/{id}      # one plugin by provider id (404 when absent)
GET    /openapi/v1.json       # OpenAPI description of the public contracts
```

## Invocation (`POST /api/invocations`)

Request:

```json
{
  "capability": "spatial.geometry.buffer@1",
  "arguments": { "geometry": { "$geometry": "…" }, "distance": 1.5 },
  "permissions": ["spatial.feature.read"],
  "deadline": "2026-09-01T00:00:00Z",
  "provider": "nts@1",
  "resource": "…token…",
  "wait": true
}
```

- `capability` — the versioned id (`name@version`).
- `arguments` — codec-encoded values (scalars, `$i64`, `$bytes`, `$geometry`,
  `$crs`, `$resource`).
- `permissions` — the scopes the caller asks to use; the host grants them per
  its own policy (security-model.md). Calls the runtime's permission gate
  enforces against the capability's declared requirements.
- `provider` — a hard pin for resolution step 1 (capability-model.md).
- `resource` — a resource token; its owning provider becomes the
  resource-local preference (step 2).
- `wait` — default `true`. `false` starts a job even for quick capabilities.

Response (`200 completed`):

```json
{
  "kind": "completed",
  "capability": "spatial.geometry.buffer@1",
  "ok": true,
  "result": { "$geometry": "…" },
  "error": null,
  "provenance": { "capability": "…", "provider": "nts@1", "step": "FirstHealthy",
                  "startedAt": "…", "durationMs": 1.2, "deadline": null, "jobId": null },
  "job": null
}
```

Response (`202 job`):

```json
{
  "kind": "job",
  "capability": "fixture.sleep@1",
  "ok": false, "result": null, "error": null, "provenance": null,
  "job": { "jobId": "…", "state": "pending", "location": "/api/jobs/…" }
}
```

The `Location` header points at the job. When `wait: true` and the serving
capability is long-running, the request never blocks: it answers `202` with
the job to poll or subscribe (ADR-0008).

## Jobs (`/api/jobs/{id}`, `/events`)

Jobs are observable state machines: `pending → running →` one terminal state
(`completed`/`failed`/`cancelled`/`timedOut`). The job snapshot carries the
serving provider and the deterministic resolution step (provenance, plan
§17.12). Events are append-only:

- `created`, `started`, `completed`, `failed`, `cancelled`, `timedOut` —
  lifecycle.
- `progress` — `{progress, message}` fraction in [0,1] or null
  (unquantified).
- `resource` — a stream/resource handle a long job published (read it
  while the job runs).
- `note` — provider diagnostics.

`GET /api/jobs/{id}/events` returns the JSON page; with
`Accept: text/event-stream` it replays past events and pushes new ones (250 ms
polling loop — the registry has no per-event push signal) until `event: done`.
Client disconnect stops the stream.

## Resources (`/api/resources/{id}`)

Opaque runtime-owned handles (ADR-0022). Metadata returns token, kind, owner,
creation time and registry state. `DELETE` disposes the resource (closes a
stream, drops leases, unblocks producers). The stream read surface:

- `GET /api/resources/{id}/stream` → `application/x-ndjson`, one
  codec-encoded item per line: `"chunk 1"`, `{"$bytes":"…"}` (canonical
  feature batches), `{"$resource":…}`. Consuming a stream to its end closes
  the resource; a stream that ended in failure carries a final
  `{"$error":{"kind","code","message"}}` line (the SDKs turn it into an
  exception/error). A client that abandons the read also closes the resource,
  so backpressured producers are always released.

## Plugins (`/api/plugins`)

The activated worker packages: provider id, display name, runtime, lifecycle
state (discovered → validated → starting → healthy → active → draining →
stopped/failed), restart count, pid, healthy-at, last error and the manifest
capabilities. The workbench's runtime-status screen and the replacement
demonstration (plan §17.9-11) read this surface.

## Configuration

| Key | Meaning |
| --- | --- |
| `Spatial:PackagesRoot` (env `Spatial__PackagesRoot`) | directory of immutable plugin packages to activate at startup (empty = no plugins) |
| `Spatial:Preferences` | configured provider preferences: `{"spatial.geometry.buffer@1": "nts@1"}` (resolution step 3) |
| `Spatial:WorkerEnvironment` | host-managed variables handed to every spawned worker (provider secrets, ADR-0028 — never through invocations) |

The host waits for its workers at startup (readiness); a package that fails
to activate is reported on `/api/plugins` (its worker state shows the error)
and never blocks the rest (the host logs each failure).

## Clients and generation

- .NET SDK: `clients/dotnet/Spatial.Client` — typed methods, the shared
  codec, `ReadFeatureBatchesAsync` (canonical binary decode), `WaitForJobAsync`.
- TypeScript SDK: `clients/typescript` — fetch-based, generated wire types,
  SFBAT decoder, `eng/e2e-web.sh` proves the live host end to end.
- The OpenAPI description is the single source for the TS wire types
  (`scripts/generate.mjs` + drift check in `npm test`).