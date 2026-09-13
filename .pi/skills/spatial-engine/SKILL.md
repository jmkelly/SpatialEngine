---
name: spatial-engine
description: Compass for working in the Spatial Engine repository (.NET spatial server + browser workbench + Esri GeoServices REST). Use when building, testing or verifying changes here; when deciding where code belongs (Spatial.Core vs Spatial.PluginSdk vs Spatial.Host vs implementation projects); when touching ingest, Esri, rendering, tiles, map composer or the Spatial CLI; when running eng/verify.sh or the quality loop; or when interpreting SpatialException codes and store selection.
---

# Spatial Engine

Spatial **values** live in `Spatial.Core`; the **verbs** are `Spatial.PluginSdk`
interfaces implemented in-process and composed by `Spatial.Host` with DI. The
browser workbench is the delivered client; Esri GeoServices REST is the interop
surface.

## Before you change anything

Run the environment doctor — it checks the pinned toolchain and prints where a
task belongs:

```bash
.pi/skills/spatial-engine/scripts/doctor.sh
```

Then route the *task* (not the symptom) through the distilled map. The ADRs in
`architecture/decisions/` win on conflict; fix a digest instead of diverging.

| Task | Read first |
| --- | --- |
| Core geometry / feature types, codecs | `architecture/distilled/core.md` |
| Service interfaces, composition | `architecture/distilled/runtime.md` |
| Implementation projects + DI lifecycle | `architecture/distilled/plugins.md` |
| Which services exist + their contracts | `architecture/distilled/contracts.md` |
| HTTP API, config, SDKs, frontend, deploy | `architecture/distilled/host-and-clients.md` |
| Esri GeoServices REST (serve/consume) | `architecture/distilled/host-and-clients.md` + `architecture/references/geoservices-compatibility.md` |
| Ingest, runtime publishing, Esri admin | `architecture/distilled/contracts.md` |
| MapServer / ImageServer, raster, tiles, labels | `architecture/distilled/rendering.md` |
| Spatial CLI, workspace / project file | `architecture/distilled/cli.md` |
| Any architectural change | `architecture/distilled/README.md` + `architecture/principles.md` |

## Hard walls (a change that breaks one needs an ADR)

- Spatial algorithms live in implementation projects; `Spatial.Core` stays
  structural — inspection, traversal, encoding, envelopes.
- Public contracts carry only core types; NTS, Npgsql, EF and renderer types
  stay inside their owning implementation.
- `Spatial.PluginSdk` references only `Spatial.Core` and takes no packages.
- The host is JIT-compiled; a Native AOT change needs an approved ADR first.
- Long-running work is a cancellable `Task`; failures are structured
  `SpatialException` codes, never strings.
- Behaviour changes land contract, SDK, test and ADR updates together, with
  success, failure and cancellation tested; pin package versions in
  `Directory.Packages.props`.

## Commands

| Command | What it does |
| --- | --- |
| `eng/verify.sh` | **The gate.** format check → build → full tests. Run before declaring done. |
| `eng/format.sh` | Format the solution (`--check` to verify only). |
| `eng/build.sh` | `dotnet build SpatialEngine.slnx` (passes through args). |
| `eng/test.sh` | Run every suite (unit, architecture, integration). |
| `eng/cli-e2e.sh` | Real host + real CLI over HTTP: ingest → styled map → MapServer. |
| `eng/e2e-web.sh` | Real host driven by the TypeScript SDK over HTTP. |
| `eng/workbench-e2e.sh` | Real host + Playwright in a browser. |
| `eng/seed.sh` | Fetch public data, ingest, publish styled feature/map services. |

`eng/verify.sh` is the definition of done. The e2e scripts need .NET 10 and
Node ≥ 22.6; `eng/seed.sh` starts and leaves a host running on
`http://127.0.0.1:5201`.

## Recipe — run the engine and drive it with the CLI

```bash
# 1. Start the independently executable host (demo store is always available).
SPATIAL_ADMIN_TOKEN=dev-token dotnet run --project src/Spatial.Host --urls http://127.0.0.1:5201 &

# 2. Readiness + catalogue.
curl -s http://127.0.0.1:5201/health/ready
curl -s http://127.0.0.1:5201/api/catalogue

# 3. Drive it through the public API only (ADR-0052 — the CLI is a client,
#    it holds no algorithm and calls no provider directly).
CLI="dotnet run --project clients/dotnet/Spatial.Cli -- --host http://127.0.0.1:5201"
$CLI host health
$CLI dataset list --store memory
$CLI map list --store memory
$CLI project plan --project spatial.json      # dry-run before apply
$CLI project apply --project spatial.json --token dev-token
```

Global options accept `--host`/`SPATIAL_HOST`, `--token`/`SPATIAL_ADMIN_TOKEN`,
`--store` (`demo`, `memory`, `postgis`; writes default `memory`), `--project`
(default `spatial.json`), `--json`, `--dry-run`, `--timeout`.

Exit codes: `0` ok, `2` invalid arguments, `3` not found, `4` store/host
unavailable, `5` cancelled, `1` unexpected. Treat these as the contract —
script against them, don't string-match output.

## Failure model

Failures surface as structured `SpatialException` codes that map to HTTP
statuses:

| Code | Meaning | Typical HTTP |
| --- | --- | --- |
| `invalid.arguments` | Bad input, unparseable style, unknown project version | 400 |
| `not.found` | Missing dataset, map or identity | 404 |
| `store.unavailable` | Store/host down, connection failed | 503 |

Never let a connection string, token or provider exception cross a service
boundary — connection strings flow from host configuration to options only.

## Change checklist

1. Route the task through the distilled map above and read the governing ADR.
2. Decide the owning project: core value → `Spatial.Core`; verb → interface in
   `Spatial.PluginSdk`, implementation in `Spatial.<Kind>.<Tech>`.
3. Land contract + SDK + tests + ADR together; test success, failure and
   cancellation.
4. Pin packages in `Directory.Packages.props`; keep `Spatial.PluginSdk`
   package-free.
5. `eng/verify.sh`, then the relevant e2e (`cli-e2e`, `e2e-web`,
   `workbench-e2e`) when the HTTP surface or a client changed.
6. Quality gates are part of done: `quality-loop` skill, repo policy
   `.dependably`, threshold `coverage-policy.json` (branch floor 70%).
