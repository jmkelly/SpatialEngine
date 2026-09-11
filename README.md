# Spatial Engine

A headless, extensible spatial engine. A small, stable .NET 10 core owns the
spatial **value model** (coordinates, geometries, CRS identity, features) and
runtime behaviour (capabilities, jobs, resources, plugin supervision).
Replaceable plugins own the spatial **verbs**: operations (buffer,
intersection, …), data providers (PostGIS first), transformations and
rendering.

The first delivered frontend is a browser-hosted React + MapLibre workbench.
A thin Tauri 2 shell packages the unchanged web client afterwards; it
contains no spatial logic.

**Key idea:** geometry is core, algorithms are plugins, and contracts outlive
implementations.

## Status

**Milestone 1 complete.** `apps/workbench-web` is a React 19 + TypeScript +
MapLibre GL workbench served by the host itself (`Spatial:WebRoot`,
ADR-0031): a provider/capability catalogue, the `demo@1` dataset browser with
map rendering and coordinate-based selection, generated capability forms, job
progress, result preview with browser persistence, runtime health, and plugin
replacement over HTTP — start `nts@2` beside `nts@1`, route new work, drain
and roll back without restarting the host. `eng/workbench-e2e.sh` builds the
app and the plugin packages, runs the real host and drives the workbench with
Playwright (plan §18; no Tauri).

The engine beneath it: `Spatial.Core` owns the value model; versioned
capability contracts ship in `Spatial.PluginSdk`, and `Spatial.Runtime` routes
to providers with permissions, deadlines and provenance; long work is a
cancellable job; and the independently executable ASP.NET Core host exposes
the public HTTP API with two generated SDKs (`clients/typescript`,
`clients/dotnet/Spatial.Client`). Providers ship for NetTopologySuite
geometry operations, ProjNet coordinate transformation, PostGIS data
(ADR-0010) and the Docker-free demo datasets.

Phase-by-phase detail lives in git history; `HANDOFF.md` records the current
risks and gotchas; `architecture/implementation-plan.md` is the source of
truth for scope and exit criteria.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Spatial.Core` | Spatial value model (no dependencies, no algorithms) |
| `src/Spatial.Runtime` | Capability registry, routing, jobs, resources, supervision |
| `src/Spatial.PluginSdk` | Public contracts and SDK for plugin developers |
| `src/Spatial.PluginHost.DotNet` | Language-neutral worker protocol for .NET plugins |
| `src/Spatial.Operations.NetTopologySuite` | Standard geometry operations (buffer, intersection, validate, simplify) |
| `src/Spatial.Transformations.ProjNet` | CRS description and coordinate transformation (spatial.crs.describe@1, spatial.coordinate.transform@1) |
| `src/Spatial.Provider.PostGIS` | Data provider: catalogue, dataset, feature scan/query/write and transactions |
| `src/Spatial.Provider.Demo` | Docker-free demo datasets and cancellable sleep for the workbench |
| `src/Spatial.Host` | Independently executable ASP.NET Core host |
| `tests/` | unit / architecture / conformance / integration suites |
| `architecture/` | Plan, principles, condensed digests and ADRs |
| `clients/ apps/` | Client SDKs and the web (later desktop) apps |
| `eng/` | Build, format, test, verify scripts |

## Requirements

- .NET SDK 10.0.400 (pinned in `global.json`)
- Node 26 (`.node-version`) — needed from Milestone 1 onward

## Quickstart

```bash
./eng/verify.sh    # format check + build + full test run
```

## Reading order for new contributors

1. `architecture/implementation-plan.md` — the full plan (source of truth)
2. `architecture/principles.md` — the twenty principles
3. `architecture/decisions/` — architecture decision records
4. `architecture/distilled/` — condensed digests, routed by task
5. `AGENTS.md` — boundaries and guidance for development agents

## Planned milestones

- **Milestone 1** — browser workbench: PostGIS → core geometry → replaceable
  buffer plugin → rendered, inspectable, persistable result (Phases 1–10).
- **Milestone 2** — Tauri 2 desktop packaging of the unchanged web client
  (Phase 11).
