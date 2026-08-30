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

Phase 5 — native plugin packaging and isolation. Immutable plugin packages
(manifest schema v1: id, version, capabilities, runtime hints — see
`architecture/plugin-manifest.md`) are discovered, validated and launched as
separate .NET worker processes over the versioned, language-neutral wire
protocol (`architecture/worker-protocol.md`, ADR-0025). The process
supervisor (`Spatial.PluginHost.DotNet`) health-checks with ping, restarts
crashed workers with backoff, activates versions side by side (routing new
work through the runtime's active-preference table, consulted between
resource-local and configured preference), drains a version without stopping
the host (wait in-flight, reclaim resources via
`ResourceRegistry.DisposeOwnerAsync`, graceful close) and rolls back to the
previous version. Resources and bounded streams stay runtime-owned across
the boundary (facility RPCs), so backpressure and leases are real for
out-of-process providers; crash, timeout and cancellation fault fixtures
exercise every path with real child processes. Phase 6 (NTS operations
plugin) is next.

Phase 4 — resources, streams and jobs. Opaque runtime-owned resource
handles with leases, disposal and leak reclamation (ADR-0022); bounded,
backpressured streaming with the `Streaming` trait enforced (ADR-0023); and
long-running invocations routed through observable, cancellable,
timeout-bounded job state machines with events and progress (ADR-0008/
ADR-0024). Versioned capability contracts (`spatial.feature.count@1`, …)
and the registry, deterministic provider resolution (explicit →
resource-local → active-preferred → configured preferred → first healthy),
invocation routing with structured errors, deadlines, cancellation and
permissions all ship in `Spatial.PluginSdk`/`Spatial.Runtime`, with the
in-memory component host as the test vehicle.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Spatial.Core` | Spatial value model (no dependencies, no algorithms) |
| `src/Spatial.Runtime` | Capability registry, routing, jobs, resources, supervision |
| `src/Spatial.PluginSdk` | Public contracts and SDK for plugin developers |
| `src/Spatial.PluginHost.DotNet` | Language-neutral worker protocol for .NET plugins |
| `src/Spatial.Host` | Independently executable ASP.NET Core host |
| `tests/` | unit / architecture / contract / conformance / integration suites |
| `architecture/` | Plan, principles, boundary docs and ADRs |
| `contracts/ clients/ apps/` | Language-neutral contracts, SDKs and web/desktop apps (Milestones 1–2) |
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
4. `AGENTS.md` — boundaries and guidance for development agents

## Planned milestones

- **Milestone 1** — browser workbench: PostGIS → core geometry → replaceable
  buffer plugin → rendered, inspectable, persistable result (Phases 1–10).
- **Milestone 2** — Tauri 2 desktop packaging of the unchanged web client
  (Phase 11).