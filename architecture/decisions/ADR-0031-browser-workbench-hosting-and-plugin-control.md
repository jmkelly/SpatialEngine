---
status: accepted
date: 2026-08-31
deciders: implementation-plan §16/§17 (Phase 10)
consulted: plugin-lifecycle.md, host-api.md
---

# ADR-0031: Browser Workbench Hosting and Plugin Control

## Context

Phase 10 ships the browser workbench (React + TypeScript + MapLibre) as
Milestone 1. The workbench must run in a normal browser against the
independently executable host (ADR-0014/0015/0018), demonstrate the
side-by-side plugin replacement of plan §17.9-11 (start version 2, route new
work to it, drain version 1 without stopping the host or the UI), and be
tested by Playwright without Docker or Tauri.

Two gaps blocked the demonstration:

1. **No browser channel to serve the workbench.** The host serves only the
   JSON API; a browser client would need CORS or a proxying middleman. The
   plan's Phase 11 packaging (Tauri) bundles assets with a sidecar host;
   Phase 10 is precisely the "unchanged React application in a normal
   browser" step.
2. **No HTTP surface for plugin control.** The supervisor's
   `RouteNewWorkTo`, `DrainAsync` and `RollbackAsync` are programmatic;
   nothing lets a running workbench start/route/drain versions.

## Decision

### 1. The host serves the built workbench as static content

When the `Spatial:WebRoot` configuration value points at a directory
containing the built React application, the host serves it as static
content: `Spatial:WebRoot` (environment `Spatial__WebRoot`) becomes the
web root, rooted through `PhysicalFileProvider` with default-file and
static-file middleware. The workbench then runs from the host itself —
same origin, no CORS, no desktop shell — mirroring what Tauri packaging
will do in Phase 11 (serve the unchanged assets beside the host).

- The static middleware runs **before** routing so ASP.NET's
  `DefaultFilesMiddleware` can win at `/` (the middleware refuses to serve
  a path routing has already matched).
- Without `Spatial:WebRoot` the host serves only the API — its default,
  tested surface is unchanged. A configured-but-missing directory is a
  startup error with an actionable message.
- With a web root, the root `GET /` serves the workbench's `index.html`
  instead of the host identity document (which remains available to
  automated clients via `/health` and `/openapi/v1.json`).

### 2. Plugin control endpoints

Three POST endpoints on the existing plugin surface delegate to the
supervisor's existing primitives (ADR-0025/0026 plugin-lifecycle): every
control action is the runtime's own documented replacement step, exposed
as HTTP with no new supervisor behaviour.

| Endpoint | Supervisor call | Semantics |
| --- | --- | --- |
| `POST /api/plugins/{id}/route-new-work` | `RouteNewWorkTo` | Active-preference routing: new work for the provider's capabilities routes here (plan §10.4). 409 when the worker cannot serve right now. |
| `POST /api/plugins/{id}/drain` | `DrainAsync` | Stop routing, wait for in-flight invocations, reclaim resources, stop the process. Idempotent. |
| `POST /api/plugins/{id}/rollback` | `RollbackAsync(package)` | Reactivate the provider's package when drained/failed and route new work back to it. 409 on activation failure. |

Responses are the updated `PluginDto` (the same shape as `GET`), so the
workbench's runtime-status screen refreshes from the response. 404 when no
supervisor is configured or the provider id is unknown.

### 3. Supporting vehicles

- **`nts@2`** — a second released NTS provider version
  (`NtsOperationsProviderV2`, same four operation contracts on the shared
  adapters) so side-by-side replacement is demonstrated with real packages.
  The shared conformance matrix pins v2 to the v1 result shapes.
- **`demo@1`** (`Spatial.Provider.Demo`) — a read-only implementation of
  the standard data-provider contracts over procedurally generated point
  datasets (catalogue list/describe, scan, bbox query) plus a
  long-running `spatial.demo.sleep@1` with progress: the Docker-free
  dataset-browser/map/progress vehicle for Playwright and local demos.
  Feature data crosses as canonical batches, metadata as JSON, like
  PostGIS (ADR-0020/0028).
- **PluginPacker** emits `nts@1`, `nts@2`, `demo@1` (and `postgis@1`) and
  now carries the providers' conformance example names into package
  manifests (the activation compatibility check compares them).

## Consequences

- The single-process browser demo `http://host/` (workbench) +
  `http://host/api/*` (contracts) is the Phase 10 shape; Phase 11's Tauri
  shell packages the same static output beside the host without changes.
- The plugin control surface is small (three endpoints, no new DTOs) and
  reuses supervisor behaviour; the replacement demonstration over HTTP is
  covered by `tests/integration/Spatial.Host.Tests/PluginReplacementTests.cs`.
- `.NET` and TypeScript SDKs gained the three typed control methods; the
  OpenAPI description gained the three paths (the TS wire types are
  unchanged — no new schemas), refreshed via `eng/e2e-web.sh`.
- `Spatial.Provider.Demo` joins the solution and is applied the standard
  architecture guards (plugin implementation, out-of-process worker).