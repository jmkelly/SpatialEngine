# Frontend Boundary

Read when working on the web workbench, TypeScript SDK or Tauri shell. See
implementation-plan.md §2.3-2.4, §12-14 and ADR-0014/0015/0016/0017/0030.

## The workbench (React + TypeScript + MapLibre)

- Talks only to the public spatial host API through the generated TypeScript
  SDK (`clients/typescript`, `architecture/host-api.md`).
- Holds engine-neutral application state; no spatial logic in the client. The
  only client-side spatial code is the **geometry adapter**: canonical SGEOM
  bytes (ADR-0020) are decoded to GeoJSON for the map (`src/sgeom.ts`, a
  byte-for-byte mirror of `GeometryCodec`) and selection matches click
  coordinates to features with pure projection math (`src/click-match.ts`) —
  deliberately renderer-independent so browser tests never read pixels back.
- Must run in a normal browser with no Tauri code or API — Playwright tests
  prove this (`tests/end-to-end-web`, driven by `eng/workbench-e2e.sh`
  against the real host serving the built app from `Spatial:WebRoot`).
- Screens: provider/catalogue browser, map with selection and attribute
  inspection, capability panel with generated forms and job progress, runtime
  status with plugin replacement views.
- Persistence: results and recent jobs are kept in browser localStorage
  (plan §17.8 "preview and persist the result"); geometry is stored as the
  canonical SGEOM base64 the host produced, never re-encoded client-side.

Bundling: MapLibre's worker script is served from the workbench root
(`public/maplibre-gl-worker.mjs`, pinned via `setWorkerUrl`) because the
static SPA build cannot emit the worker file MapLibre's runtime-computed URL
would need.

The TypeScript SDK's wire types are *generated* from the host's OpenAPI
description (`clients/typescript/scripts/generate.mjs`) and drift-checked in
`npm test`; `eng/e2e-web.sh` drives the real host from Node as the
browser-compatible automated client (Phase 9).

## The Tauri shell

- Packages React production assets; window, lifecycle, sidecar supervision,
  narrow native adapters, install/update.
- Contains **no** geometry, spatial operations, provider logic, capability
  resolution or project-domain behaviour (ADR-0017).

## Workbench invariants

- `package.json` dependencies must never include `@tauri-apps/*` — enforced
  by `Web_clients_do_not_depend_on_tauri` in tests/architecture.
- Desktop delivery runs the unchanged browser workbench; added value is
  packaging and lifecycle only.