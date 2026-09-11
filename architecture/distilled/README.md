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
| Service interfaces, implementations, composition | `runtime.md` | 0033 |
| Implementation projects and DI lifecycle | `plugins.md` | 0033 |
| Which services exist + their contracts | `contracts.md` | 0033 |
| HTTP API, config, SDKs, frontend, deployment, secrets | `host-and-clients.md` | 0014–0019, 0033 |
| Esri GeoServices REST (serve/consume) | `../geoservices-implementation-plan.md`, `../references/geoservices-compatibility.md` | 0035 |
| Any architectural change | this file + `../principles.md` | — |

## The twenty principles (see ../principles.md)

Superseded by ADR-0033 where they assumed worker plugins; the standing
shape is noted in brackets.

1. Geometry is core; spatial algorithms are not.
2. Headless engine — every UI is a client.
3. Browser workbench is the first frontend.
4. Tauri is packaging, not architecture.
5. The .NET host runs independently of Tauri.
6. Contracts outlive implementations. [Interfaces in `Spatial.PluginSdk`.]
7. Implementations depend on contracts, never on each other.
8. No implementation-specific geometry object crosses a service boundary.
9. Core geometry values are immutable.
10. Data stores are providers, not the domain model.
11. Long-running operations are cancellable `Task`s. [No job model.]
12. Persistent state is external. [Implementations are linked, not disposable workers.]
13. Open formats at boundaries. [In-process DI; canonical binary on HTTP.]
14. Agents and human clients use the same public services.
15. Provider pushdown is optional and must preserve contract semantics.
16. Every derived result records its service. [No provenance envelopes.]
17. Kernel stays small, stable, independently testable.
18. Rust only where profiling or platform integration justifies it.
19. No Native AOT until compatibility is demonstrated.
20. Desktop and browser behaviour share the same conformance tests.

## ADR register (one line each; full records in `../decisions/`)

| ADR | Decision in one line |
| --- | --- |
| 0001 | Geometry values are core; algorithms are never core. |
| 0002 | ~~Spatial operations are capability plugins~~ — superseded by 0033. |
| 0003 | ~~Data stores are provider plugins~~ — superseded by 0033. |
| 0004 | Core geometry values are immutable. |
| 0005 | Third-party types (NTS, Npgsql, EF, renderer) never cross public contracts. |
| 0006 | ~~Isolated worker processes~~ — superseded by 0033 (in-process default). |
| 0007 | ~~Versioned capability contracts~~ — superseded by 0033 (typed interfaces). |
| 0008 | ~~Jobs for long operations~~ — superseded by 0033 (cancellable Tasks). |
| 0009 | CRS identity is core; transformation is a service. |
| 0010 | PostGIS is the initial backing provider. |
| 0011 | .NET 10 backend/runtime; boundaries stay core-typed. |
| 0012 | Host is JIT-compiled initially. |
| 0013 | ~~Language-neutral worker boundaries~~ — superseded by 0033. |
| 0014 | React + TypeScript + MapLibre frontend; talks only to the public host API. |
| 0015 | Browser milestone completes before any Tauri work. |
| 0016 | Tauri 2 is the desktop shell; narrow native integration, WebView2 first. |
| 0017 | Tauri contains no spatial logic; native adapters are narrow capabilities. |
| 0018 | `Spatial.Host` is independently executable; API identical for every client. |
| 0019 | Tauri may bundle Spatial.Host as an optional sidecar; remote-host mode stays supported. |
| 0020 | Canonical binary interchange is the required wire format; JSON only for debugging/metadata. |
| 0021 | Native AOT only with measured benefit + compatibility evidence; main host stays JIT. |
| 0022 | ~~Runtime-owned resource handles~~ — superseded by 0033 (store-owned handles). |
| 0023 | ~~Bounded backpressured streams~~ — superseded by 0033 (batch pages). |
| 0024 | ~~Observable job state machines~~ — superseded by 0033. |
| 0025 | ~~Line-delimited JSON worker wire~~ — superseded by 0033. |
| 0026 | ~~Versioned geometry operation contracts~~ — superseded by 0033 (`IGeometryOperations`). |
| 0027 | ~~Transformation contracts~~ — superseded by 0033 (`ICrsDirectory`/`ICoordinateTransforms`). |
| 0028 | ~~PostGIS provider contracts~~ — superseded by 0033 (`IDataCatalogue`/`IFeatureStore`/`ITransactionStore`). |
| 0029 | Feature model gains contract faces (`IFeature`…); `FeatureBatchCodec` → `Core.Features.Codec`. |
| 0030 | ~~Host API capability envelopes~~ — superseded by 0033 (typed routes). |
| 0031 | ~~Workbench hosting + plugin control~~ — superseded by 0033 (static serving stays, control endpoints removed). |
| 0032 | Geometry contract faces (`IPoint`…`IGeometryFactory`); `GeometryCodec` → `Core.Geometry.Codec`. |
| 0033 | In-process service interfaces + DI replace worker plugins. |
| 0034 | Aspire AppHost composes the local development profile. |
| 0035 | GeoServices REST is an adapter-owned boundary; ArcGIS REST is consumed as a provider. |
| 0036 | Geometry measurement, processing and relation verbs are separate SDK interfaces. |

## How to change the architecture

1. Decision change → new/updated ADR in `architecture/decisions/` **first**.
2. New service → interface + implementation + tests, together.
3. New package in a platform project → ADR required before the architecture-test allowlist accepts it.
4. Enforcement lives in `tests/architecture/Spatial.Architecture.Tests`; every rule names the principle/ADR it implements.
5. Public changes update contracts, SDKs, tests, ADRs **and the relevant distilled doc** together (plan §20/§22).

## Documentation health notes

- ADRs 0002/0003/0006–0008/0013/0022–0028/0030/0031 describe the retired
  worker-plugin model; they are kept as history and marked superseded
  above. Read them for rationale, not for current structure.
- Byte-level wire specs: `GeometryCodec`/`FeatureBatchCodec` source is the
  authoritative format spec; `core.md` carries the essentials.
