# Distilled Architecture

The condensed architecture documentation for this repo. Kept deliberately
small for fast LLM/human consumption. **Precedence when documents disagree:**
`architecture/decisions/` (ADRs) and `architecture/implementation-plan.md`
beat these digests; fix a digest instead of diverging from an ADR. The ADRs
are dated decision records — their prose reflects the state at decision time.

## Route by task

| Task | Read | Related ADRs |
| --- | --- | --- |
| Core geometry / feature types, codecs | `core.md` | 0001, 0004, 0009, 0020, 0029, 0032 |
| Capability registry, resolution, invocation, resources, streams, jobs | `runtime.md` | 0002, 0003, 0007, 0008, 0022–0024 |
| Plugin packages, manifests, worker wire protocol, lifecycle | `plugins.md` | 0002, 0006, 0013, 0025 |
| Which capabilities exist + their contracts | `contracts.md` | 0026, 0027, 0028 |
| HTTP API, config, SDKs, frontend, deployment, secrets | `host-and-clients.md` | 0014–0019, 0030, 0031 |
| The twenty principles | `../principles.md` | — |
| Any architectural change | this file + `../principles.md` | — |

## ADR register (one line each; full records in `../decisions/`)

| ADR | Decision in one line |
| --- | --- |
| 0001 | Geometry values are core; algorithms are never core. |
| 0002 | Spatial operations are capability plugins (`spatial.geometry.buffer@1` …). |
| 0003 | Data stores are provider plugins with small contracts, not one `ISpatialDataStore`. |
| 0004 | Core geometry values are immutable. |
| 0005 | Third-party types (NTS, Npgsql, EF, renderer) never cross public contracts. |
| 0006 | Plugins default to isolated worker processes; in-process ALC / WASM only for justified cases. |
| 0007 | Capability contracts are versioned independently (`name@version`). |
| 0008 | Long-running operations are jobs; always cancellable; timeouts + structured diagnostics. |
| 0009 | CRS identity is core; transformation is a plugin. |
| 0010 | PostGIS is the initial backing provider. |
| 0011 | .NET 10 backend/runtime; worker boundaries stay language-neutral. |
| 0012 | Host is JIT-compiled initially. |
| 0013 | Worker boundaries are language-neutral; no .NET/NTS/Npgsql/EF/ASP.NET/Tauri types cross. |
| 0014 | React + TypeScript + MapLibre frontend; talks only to the public host API. |
| 0015 | Browser milestone completes before any Tauri work. |
| 0016 | Tauri 2 is the desktop shell; narrow native integration, WebView2 first. |
| 0017 | Tauri contains no spatial logic; native adapters are narrow capabilities. |
| 0018 | `Spatial.Host` is independently executable; API identical for every client. |
| 0019 | Tauri may bundle Spatial.Host as an optional sidecar; remote-host mode stays supported. |
| 0020 | Canonical binary interchange is the required wire format; JSON only for debugging/metadata. |
| 0021 | Native AOT only with measured benefit + compatibility evidence; main host stays JIT. |
| 0022 | Resource handles are runtime-owned opaque ids with leases, disposal, leak reclamation. |
| 0023 | Bounded streams carry backpressure; the `Streaming` trait is enforced. |
| 0024 | Jobs are observable state machines (`pending → running → completed/failed/cancelled/timedOut`). |
| 0025 | Worker wire = line-delimited JSON over stdio, versioned envelope `spatial.worker/1`, facility RPCs. |
| 0026 | Four standard geometry ops are versioned contracts; inline, cancellable, pure. |
| 0027 | CRS describe + transform contracts; ProjNet adapter; x-first axis convention; `$crs` wire tag. |
| 0028 | Nine PostGIS provider contracts; feature data is canonical binary; secrets via worker launch environment. |
| 0029 | Feature model gains contract faces (`IFeature`…); `FeatureBatchCodec` → `Core.Features.Codec`. |
| 0030 | HTTP host API shares the SDK DTOs + `Spatial.PluginSdk.Codec.ValueCodec` (one codec everywhere). |
| 0031 | Host serves the workbench statically (`Spatial:WebRoot`); plugin control endpoints; `nts@2`/`demo@1`. |
| 0032 | Geometry contract faces (`IPoint`…`IGeometryFactory`); `GeometryCodec` → `Core.Geometry.Codec`. |

## How to change the architecture

1. Decision change → new/updated ADR in `architecture/decisions/` **first**.
2. New capability → versioned contract + conformance fixtures + SDK updates, together.
3. New package in a platform project → ADR required before the architecture-test allowlist accepts it.
4. Enforcement lives in `tests/architecture/Spatial.Architecture.Tests`; every rule names the principle/ADR it implements.
5. Public changes update contracts, SDKs, tests, ADRs **and the relevant distilled doc** together (plan §20/§22).

## Documentation health notes

- The former per-topic docs (`core-boundary.md`, `geometry-model.md`,
  `capability-model.md`, `host-api.md`, …) were consolidated into these
  digests; recover them from git history if a detail is missing.
- ADRs 0025–0032 embed ephemeral metric snapshots (Ca 7/9, abstractness
  0.31) — treat those as historical context, not live constraints. Live
  constraint: `Core.Geometry` hub abstractness ≥ 0.3; codec namespaces are leaves.
- `implementation-plan.md` §16 phase statuses duplicate git history; use the
  plan for exit criteria and scope, not current progress.
- Byte-level wire specs: `GeometryCodec`/`FeatureBatchCodec` source is the
  authoritative format spec; `core.md` carries the essentials.
