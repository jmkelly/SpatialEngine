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

Phase 2 — features and schemas. The boxing-free `AttributeValue` union,
field definitions, schema compatibility rules (append-only prefix), feature
and batch values, and the canonical batch codec (v1) are implemented with
unit, seeded-property and malformed-input tests. Phase 3 (capability
runtime) is next.

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