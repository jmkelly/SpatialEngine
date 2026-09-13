# Spatial Engine

Spatial **values** live in `Spatial.Core`; the **verbs** are `Spatial.PluginSdk`
interfaces implemented in-process and composed by `Spatial.Host` with DI. The
browser workbench is the delivered client; Esri GeoServices REST is the
interop surface.

## Hard walls

- Spatial algorithms live in implementation projects; `Spatial.Core` stays
  structural: inspection, traversal, encoding, envelopes.
- Public contracts carry only core types; NTS, Npgsql, EF and renderer types
  stay inside their owning implementation.
- `Spatial.PluginSdk` references only `Spatial.Core` and takes no packages.
- The host is JIT-compiled; a Native AOT change needs an approved ADR first.
- Long-running work is a cancellable `Task`; failures are structured
  `SpatialException` codes (`invalid.arguments`, `not.found`,
  `store.unavailable`).
- Behaviour changes land contract, SDK, test and ADR updates together, with
  success, failure and cancellation tested; pin package versions in
  `Directory.Packages.props`.

## Commands

- `eng/verify.sh` — format, build, full tests; the gate before done.
- `eng/e2e-web.sh`, `eng/workbench-e2e.sh` — real host + delivered clients.
- `eng/seed.sh` — on-demand realistic dataset: fetch public data, ingest
  (with engine-side reprojection) and publish styled feature/map services.
- Quality loop: read the `quality-loop` skill first; repo policy is
  `.dependably` and `coverage-policy.json`.

## Detail on demand

- Architecture, contracts, host, Esri, ingest, rendering — route by task
  through `architecture/distilled/README.md`; the ADRs in
  `architecture/decisions/` win on conflict.
