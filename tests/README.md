# Tests

- `unit/` — per-project unit tests (core, operations, transformations, Esri
  interop, GeoServices adapter, providers, the .NET client SDK)
- `architecture/` — dependency and structure guardrails
- `integration/` — host API tests and the PostGIS container suites
  (skipped without Docker). The host suite starts one PostGIS container per
  test process and configures the store from it (ADR-0072), so running the
  tests needs no `SPATIAL_POSTGIS_CONNECTION`
- `end-to-end-web/` — Playwright tests for the browser workbench
- `fixtures/` — the fault-fixture services and captured ArcGIS REST fixtures
