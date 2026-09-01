# Spatial Engine

Headless, extensible spatial engine: a small .NET 10 core owns spatial
**values** and runtime behaviour; replaceable plugins own the spatial
**verbs** (algorithms, stores, transformations, rendering). The browser
workbench ships first; Tauri packages it later. Source of truth:
`architecture/implementation-plan.md`; decisions: `architecture/decisions/`.

## Boundaries (hard walls — enforced by tests/architecture)

- Geometry values live in `Spatial.Core`; spatial algorithms ship in plugins.
  The core stays structural: inspection, traversal, encoding, envelopes.
- Public contracts carry only core types. NTS, Npgsql, EF, renderer and
  Rust/Tauri types stay inside their owning plugin, never crossing a
  boundary.
- `Spatial.Runtime` routes to capabilities through contracts; it never
  references a plugin implementation. Plugin implementations are launched as
  workers, not linked in.
- `Spatial.Host` runs standalone with no desktop shell. The browser workbench
  runs without Tauri; the Tauri shell (Milestone 2) contains no spatial logic.
- The host is JIT-compiled. Native AOT requires an approved ADR plus measured
  benefit and compatibility evidence.
- Long-running behaviour is a cancellable job with structured diagnostics
  and provenance.
- Public changes update contracts, SDKs, tests and ADRs together; versions
  live in `Directory.Packages.props`, never inline.

## Where to look

- Start here → `architecture/distilled/` (condensed docs; `README.md` routes by task)
- Core/geometry changes → `architecture/distilled/core.md`, `src/Spatial.Core/AGENTS.md`
- Capabilities/runtime → `architecture/distilled/runtime.md`
- Plugins/workers → `architecture/distilled/plugins.md`
- Capability contracts → `architecture/distilled/contracts.md`
- Host API, workbench, deployment, secrets → `architecture/distilled/host-and-clients.md`

## Commands

- `eng/verify.sh` — format check + build + full test run; required before done
- `eng/build.sh`, `eng/test.sh`, `eng/format.sh [--check]`

## Definition of done (plan §22)

Implemented behaviour with versioned contracts; success, failure and
cancellation tested; no prohibited dependency; actionable diagnostics;
docs/ADRs updated; clean `eng/verify.sh` from a clean checkout.