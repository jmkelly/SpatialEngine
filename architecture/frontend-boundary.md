# Frontend Boundary

Read when working on the web workbench, TypeScript SDK or Tauri shell. See
implementation-plan.md §2.3-2.4, §12-14 and ADR-0014/0015/0016/0017/0030.

## The workbench (React + TypeScript + MapLibre)

- Talks only to the public spatial host API through the generated TypeScript
  SDK (`clients/typescript`, `architecture/host-api.md`).
- Holds engine-neutral application state; no spatial logic in the client.
- Must run in a normal browser with no Tauri code or API — Playwright tests
  prove this.
- Screens: provider/catalogue browser, map with selection and attribute
  inspection, capability panel with generated forms and job progress, runtime
  status with plugin replacement views.

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