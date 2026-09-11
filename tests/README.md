# Tests

- `unit/` — per-project unit tests (core, operations, runtime, plugin host,
  providers, transformations and the .NET client SDK)
- `architecture/` — dependency and structure guardrails
- `conformance/` — shared fixtures every provider of a capability contract
  must pass
- `integration/` — host API, PostGIS container and worker-process tests
- `end-to-end-web/` — Playwright tests for the browser workbench
- `fixtures/` — the fault-fixture plugin the worker protocol tests package
