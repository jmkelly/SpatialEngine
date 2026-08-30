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

Phase 8 — PostGIS data provider. The data-provider contracts ship in
`Spatial.PluginSdk.Providers` (`spatial.catalogue.list@1`,
`spatial.dataset.describe@1`, `spatial.dataset.create@1`,
`spatial.feature.scan@1`, `spatial.feature.query@1`,
`spatial.feature.write@1`, `spatial.transaction.begin@1` / `commit@1` /
`rollback@1` — ADR-0028, `architecture/data-provider-contracts.md`), and
`Spatial.Provider.PostGIS` (`postgis@1`) implements them on Npgsql 10.
Schema discovery (information_schema/geometry_columns/pg_class), streaming
feature scans with canonical binary batches both directions
(`ST_AsEWKB`/`ST_GeomFromEWKB`, ADR-0020), bounding-box and parameterised
attribute filtering (every literal bound, columns resolved against
discovered fields), single-transaction appends, result-table creation and
commit/rollback/enlisted transactions are covered by 128 DB-free unit
tests, a shared conformance matrix (in-process and as a packaged worker)
and a containerised integration suite (Testcontainers PostGIS) that also
proves mid-stream database cancellation and secret redaction. Secrets stay
host-managed: the supervisor hands providers their connection configuration
through the worker launch environment (`SPATIAL_POSTGIS_CONNECTION`), never
through invocations.

Phase 7 — coordinate transformation plugin. The CRS description and
coordinate transformation contracts ship in
`Spatial.PluginSdk.Transformations` (`spatial.crs.describe@1` /
`spatial.coordinate.transform@1`, ADR-0027,
`architecture/transformation-contracts.md`), and
`Spatial.Transformations.ProjNet` (`projnet@1`) implements them on ProjNet
2.1 with a private adapter over a curated embedded EPSG catalogue (WGS 84,
ETRS89, NAD83, OSGB36, RGF93; Web Mercator, UTM zones, British National
Grid, Lambert-93). The engine's x-first coordinate convention (x is
longitude/easting) is ProjNet's own math-transform order, so no axis swaps
are needed; describe reports the declared axes. Control-point, axis-order,
error and tolerance tests pin the adapter against authoritative PROJ-9
values to sub-centimetre for datum-free pairs and 0.1 m for the
Helmert-based OSGB36 path. Results keep Z/M and layout and are stamped with
the target CRS; CRS descriptions cross the worker boundary as a new `$crs`
wire tag (`$geometry` stays the interchange for transformed geometry,
ADR-0020). The shared conformance suite runs the same fixtures in-process
and as an isolated worker package.

Phase 6 — NetTopologySuite operations plugin. The standard geometry
operation contracts ship in `Spatial.PluginSdk.Operations`
(`spatial.geometry.buffer@1` / `intersection@1` / `validate@1` /
`simplify@1`, ADR-0026, `architecture/operation-contracts.md`), and
`Spatial.Operations.NetTopologySuite` (`nts@1`) implements them with a
private adapter that never exposes NetTopologySuite types (ADR-0005):
buffer, intersection, OGC validity (an invalid geometry is a successful
`false`) and Douglas-Peucker simplification, with the input CRS identity
carried onto results. Geometry crosses the worker boundary as canonical
binary interchange in a `$geometry` wire tag (ADR-0020) — never JSON
geometry. A shared conformance suite (`tests/conformance`, plan §18) runs
the same success / empty-input / unsupported-input / cancellation /
diagnostics / provenance fixtures against the provider both in-process and
as an isolated worker package, and every invocation outcome carries
provenance naming the serving provider.

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
exercise every path with real child processes.

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
| `src/Spatial.Operations.NetTopologySuite` | Standard geometry operations (buffer, intersection, validate, simplify) |
| `src/Spatial.Transformations.ProjNet` | CRS description and coordinate transformation (spatial.crs.describe@1, spatial.coordinate.transform@1) |
| `src/Spatial.Provider.PostGIS` | Data provider: catalogue, dataset, feature scan/query/write and transactions |
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