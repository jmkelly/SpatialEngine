# Spatial Engine Implementation Plan

> **Status:** Updated architectural plan  
> **Architecture:** Geometry-centred spatial microkernel  
> **Backend:** .NET 10, initially JIT-compiled  
> **Initial data provider:** PostGIS  
> **First frontend:** React, TypeScript and MapLibre in a normal browser  
> **Desktop delivery:** Tauri 2 after the browser vertical slice is proven  
> **Desktop runtime:** Independently executable .NET host, optionally bundled as a Tauri sidecar  
> **Development model:** Contract-first, test-driven and agent-friendly

## 1. Executive Summary

Build a headless, extensible spatial engine in which the core defines stable spatial values and runtime behaviour, while replaceable plugins provide spatial operations, persistence, coordinate transformation, rendering, import/export and domain functionality.

The core owns the **spatial nouns**:

- Coordinates and coordinate sequences
- Geometry types and structure
- Coordinate reference identity
- Features, attributes and schemas
- Capability contracts
- Plugin discovery and lifecycle
- Invocation routing
- Resource handles and streams
- Jobs, cancellation, permissions and diagnostics

Plugins own the **spatial verbs and integrations**:

- Buffer, intersect, union, simplify and validate
- Coordinate transformations
- PostGIS and other data stores
- Spatial indexes
- Vector tiles and map rendering
- Raster processing
- Import and export codecs
- Project persistence
- Workflow and domain tools

The first delivery is a browser-hosted React workbench connected to an independently executable .NET spatial host. Tauri is introduced only after this browser vertical slice is working. The Tauri application packages the existing web client and may launch the same .NET host as a sidecar. It contains no spatial business logic.

The first vertical slice must prove:

```text
PostGIS
  -> core Feature and Geometry values
  -> dynamically loaded geometry operation
  -> result displayed in a browser-hosted map
  -> result optionally written to PostGIS
  -> operation plugin replaced without restarting the engine
  -> unchanged web client later packaged with Tauri
```

## 2. Technology Decisions

### 2.1 Backend and runtime

Use **.NET 10** for the initial spatial host, runtime and first-party .NET plugin SDK.

Initial deployment mode:

```text
JIT-compiled .NET host
Self-contained .NET plugin workers
ASP.NET Core Minimal APIs
Local or remote gRPC for worker communication
WebSockets or server-sent events for job progress
```

Do not begin with Native AOT. Dynamic loading, diagnostics and plugin development are more important than reducing executable size during the initial architecture work. Native AOT may be evaluated later for narrowly scoped executables or workers that do not require dynamic managed loading.

### 2.2 Language-neutral boundaries

Choosing .NET for the host must not require every plugin to use .NET.

All worker boundaries must use versioned, language-neutral contracts so that future providers may be implemented in:

- .NET
- Rust
- WebAssembly
- Python
- Another process or remote service

The following must never cross a public process boundary:

- `NetTopologySuite.Geometry`
- `Npgsql` types
- Entity Framework entities
- ASP.NET Core request objects
- Tauri or Rust application types
- Renderer-specific objects

### 2.3 Frontend

The first frontend is:

```text
React
TypeScript
MapLibre GL JS
Generated TypeScript SDK
Engine-neutral application state
Normal browser delivery
```

The browser workbench is Milestone 1. It must work without Tauri and communicate only through the public spatial host API.

### 2.4 Desktop shell

Use **Tauri 2** as the planned first desktop shell after the browser vertical slice is proven.

Tauri responsibilities are limited to:

- Native application window and lifecycle
- Packaging React production assets
- Optionally launching and supervising the .NET spatial host sidecar
- Native file picker and other explicitly approved desktop adapters
- Desktop installation and update integration
- Clean shutdown of a bundled local host

Tauri must not contain:

- Geometry logic
- Spatial operations
- Data-provider logic
- Capability resolution
- Project-domain behaviour
- A separate desktop-only spatial API

### 2.5 Deployment profiles

#### Browser/server profile

```text
React web assets
ASP.NET Core Spatial.Host
External PostGIS
Out-of-process plugin workers
```

#### Local development profile

```text
.NET Aspire composition
PostGIS container
Spatial.Host
React development server
Plugin worker processes
Browser
```

#### Desktop profile

```text
Tauri 2 package
React production assets
Self-contained Spatial.Host sidecar or configured remote host
Selected built-in plugins
Optional remote providers
```

The same public API, contracts, TypeScript SDK and React application are used in every profile.

## 3. Product Vision

Create a modern spatial capability runtime through which people, applications and AI agents can discover data, invoke spatial capabilities, compose workflows and inspect results using stable, open contracts.

This is not initially intended to reproduce every desktop GIS function. It is a foundation for building focused spatial products without coupling them to a monolithic vendor platform.

### 3.1 Product principles

1. **Geometry is core. Spatial algorithms are not.**
2. **The engine is headless. Every UI is a client.**
3. **The browser workbench is the first frontend.**
4. **Tauri is packaging and native integration, not the application architecture.**
5. **The .NET host runs independently of Tauri.**
6. **Contracts outlive implementations.**
7. **Plugins depend on contracts, never on other plugin implementations.**
8. **No plugin-specific geometry object crosses a capability boundary.**
9. **Core geometry values are immutable.**
10. **Data stores are providers, not the domain model.**
11. **Long-running operations are jobs and are always cancellable.**
12. **Plugin code is disposable. Persistent state is external.**
13. **Open formats and language-neutral protocols are preferred at boundaries.**
14. **Agents and human clients use the same public capabilities.**
15. **Optimised provider pushdown is optional and preserves contract semantics.**
16. **Every derived result records provenance.**
17. **The kernel remains small, stable and independently testable.**
18. **Use Rust only where profiling or platform integration justifies it.**
19. **Do not introduce Native AOT until compatibility is demonstrated.**
20. **Desktop and browser behaviour must be covered by the same conformance tests.**

## 4. Scope

### 4.1 Initial scope

- Immutable core geometry model
- Minimal feature and schema model
- Versioned capability contracts
- Plugin package and manifest format
- Out-of-process plugin supervision
- In-process trusted plugin support where justified
- Capability registry and deterministic resolution
- Invocation, streaming, cancellation and progress
- PostGIS data provider
- NetTopologySuite geometry operations provider
- Coordinate transformation provider
- ASP.NET Core host API
- Generated TypeScript and .NET SDKs
- React and MapLibre browser workbench
- Plugin side-by-side upgrade and draining
- Contract, conformance and integration test suites
- Agent instructions and bounded work packages
- Tauri desktop packaging after the browser milestone

### 4.2 Explicitly out of scope for the first browser milestone

- Tauri packaging
- Complete ArcGIS Desktop or QGIS feature parity
- Native AOT deployment
- Raster analytics
- 3D globe rendering
- Point-cloud processing
- Full offline synchronisation
- Plugin marketplace and commercial billing
- Distributed workflow orchestration
- Automatic provider cost optimisation
- Multi-user collaborative editing
- Advanced topology model
- Curved geometry types
- Mobile-native UI

## 5. Target Architecture

```text
┌──────────────────────────────────────────────────────────────┐
│ Delivery Environments                                        │
│ Browser first │ Tauri desktop second │ Future clients        │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│ React + TypeScript + MapLibre                                │
│ Generated SDK │ Engine-neutral state │ No spatial logic      │
└──────────────────────────────┬───────────────────────────────┘
                               │
                     HTTP / gRPC / WebSocket
                               │
┌──────────────────────────────▼───────────────────────────────┐
│ Independently Executable .NET 10 Spatial.Host                │
│ ASP.NET Core API │ Authentication │ Streaming │ Health       │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│ Spatial Core and Runtime                                     │
│ Geometry │ CRS Identity │ Feature │ Schema                  │
│ Capability Registry │ Router │ Handles │ Streams            │
│ Plugin Supervisor │ Jobs │ Policy │ Diagnostics             │
└───────────────┬───────────────────┬──────────────────────────┘
                │                   │
       ┌────────▼────────┐  ┌───────▼─────────────────────────┐
       │ Operation       │  │ Provider Plugins                │
       │ Plugins         │  │ PostGIS │ GeoParquet │ COG     │
       │ NTS │ GEOS      │  │ ArcGIS REST │ OGC APIs         │
       │ PROJ │ WASM     │  │ Object Storage                 │
       └─────────────────┘  └─────────────────────────────────┘
```

Tauri later wraps the React assets and either launches `Spatial.Host` as a sidecar or connects to a configured remote host.

## 6. Core Boundary

A component belongs in the core only when all of the following are true:

1. Almost every spatial plugin must exchange it.
2. Independently developed plugins must agree on its meaning.
3. It can be represented without choosing a spatial algorithm.

### 6.1 Core responsibilities

#### Spatial value model

- `Coordinate`
- `CoordinateLayout`
- `ICoordinateSequence`
- `Envelope`
- `IGeometry`
- Concrete simple-feature geometry types
- `CoordinateReference`
- `Feature`
- `FeatureId`
- `AttributeValue`
- `FieldDefinition`
- `FeatureSchema`

#### Runtime model

- Component and capability identity and versions
- Plugin manifest validation
- Capability registration and lookup
- Invocation routing
- Resource-handle ownership
- Stream routing and backpressure
- Job creation, status, progress and cancellation
- Permission evaluation
- Health reporting
- Diagnostics and structured errors
- Contract compatibility checking

### 6.2 Core exclusions

The core must not implement:

- Buffer, intersection, union or difference
- Spatial predicates
- Distance, area or length
- Centroid or simplification
- Geometry validation or repair
- Coordinate transformation
- Spatial indexing
- SQL or data-store behaviour
- Rendering, styling or tiling
- Project persistence
- Workflow orchestration
- Desktop-shell behaviour

### 6.3 Permitted structural behaviour

The core may provide:

- Geometry type and empty-state inspection
- Coordinate enumeration and counts
- Coordinate-layout inspection
- Component traversal
- Envelope calculation from coordinate minima and maxima
- Canonical encoding and decoding
- Structural equality

## 7. Core Geometry Design

Initial geometry types:

```text
Point
LineString
Polygon
MultiPoint
MultiLineString
MultiPolygon
GeometryCollection
```

Core geometry values are immutable and are not aliases for NetTopologySuite objects.

```csharp
public readonly record struct Coordinate(
    double X,
    double Y,
    double? Z = null,
    double? M = null);

public readonly record struct CoordinateReference(
    string Authority,
    string Code);

public interface ICoordinateSequence
{
    int Count { get; }
    CoordinateLayout Layout { get; }
    double GetOrdinate(int index, Ordinate ordinate);
}

public interface IGeometry
{
    GeometryType Type { get; }
    CoordinateLayout Layout { get; }
    CoordinateReference? CoordinateReference { get; }
    bool IsEmpty { get; }
    int CoordinateCount { get; }
    Envelope? Envelope { get; }
}
```

Initial coordinate-sequence implementations:

- Packed double sequence
- Array sequence

Future implementations may use shared memory, memory mapping, WKB backing or Arrow without changing geometry contracts.

## 8. Canonical Interchange

Initial interchange supports:

- WKB or EWKB-derived canonical binary geometry
- Versioned wrapper carrying CRS and coordinate-layout metadata
- JSON for debugging and public API usability
- Versioned feature batches
- Opaque handles for expensive or long-lived resources

Use:

1. Inline values for points, envelopes, options and small geometries.
2. Streams for feature batches, tiles and progressive results.
3. Opaque handles for datasets, transactions and intermediate results.

The runtime must not require geometry to pass through JSON between workers.

## 9. Capability Model

Use stable, versioned identifiers:

```text
spatial.geometry.buffer@1
spatial.geometry.intersection@1
spatial.geometry.validate@1
spatial.coordinate.transform@1
spatial.feature.scan@1
spatial.feature.query@1
spatial.feature.write@1
spatial.map.render@1
```

Every capability defines:

- Identifier and version
- Purpose
- Input and output schemas
- Error variants
- Required permissions
- Side effects
- Streaming and cancellation behaviour
- Provenance fields
- Conformance examples

Initial provider resolution is deterministic:

1. Explicit provider requested by caller
2. Compatible resource-local provider
3. Configured preferred provider
4. First healthy provider by stable provider ID

A store may provide pushdown operations, but it must implement the same capability contract and pass the same conformance tests.

## 10. Plugin Model

### 10.1 Trusted in-process plugins

- Loaded using a collectible `AssemblyLoadContext`
- Reserved for controlled, latency-sensitive components
- Not treated as a security boundary
- Not the default for third-party plugins

### 10.2 Isolated worker plugins

This is the default for substantial plugins.

- Separate executable process
- Local gRPC or equivalent language-neutral protocol
- Crash isolation
- Independent restart and replacement
- Resource controls
- Side-by-side versions and draining

### 10.3 WebAssembly plugins

Use for portable, permission-constrained computational components after the native-worker model is established.

### 10.4 Replacement lifecycle

```text
Discovered -> Validated -> Starting -> Healthy -> Active
                                             -> Draining -> Stopped
```

Replacement sequence:

1. Validate the new immutable package.
2. Start it beside the active version.
3. Run health and compatibility checks.
4. Route new work to the new version.
5. Drain the previous version.
6. Stop it after current work completes or reaches a cancellation boundary.
7. Retain rollback metadata.

## 11. Data Provider Contracts

Avoid a single large `ISpatialDataStore` interface. Use small capabilities:

```text
spatial.catalogue.list@1
spatial.catalogue.search@1
spatial.dataset.describe@1
spatial.dataset.create@1
spatial.feature.scan@1
spatial.feature.query@1
spatial.feature.append@1
spatial.feature.update@1
spatial.feature.delete@1
spatial.transaction.begin@1
spatial.transaction.commit@1
spatial.transaction.rollback@1
```

The initial PostGIS provider supports:

- Host-managed connection secrets
- Dataset discovery and schema description
- Bounding-box and parameterised attribute filtering
- Bounded feature streaming
- Feature append and result-table creation
- Transactions
- Core geometry conversion
- Database-command cancellation
- Structured diagnostics

## 12. Host API and SDKs

The .NET host is independently executable and must not assume it is running under Tauri.

Initial endpoints:

```text
GET  /api/capabilities
GET  /api/capabilities/{id}
POST /api/invocations
GET  /api/jobs/{id}
POST /api/jobs/{id}/cancel
GET  /api/jobs/{id}/events
GET  /api/resources/{id}/metadata
DELETE /api/resources/{id}
GET  /api/plugins
GET  /api/plugins/{id}
GET  /health/ready
GET  /health/live
```

Generate:

- TypeScript SDK for React and other web clients
- .NET SDK for automation and service clients
- OpenAPI description for public HTTP contracts

## 13. Browser Workbench, Milestone 1

### 13.1 Technology

- React
- TypeScript
- MapLibre GL JS
- Generated TypeScript SDK
- Engine-neutral state store
- Playwright end-to-end testing

### 13.2 First screens

#### Provider and catalogue

- Display configured providers
- Browse datasets
- Inspect schemas and CRS
- Add a dataset to the map

#### Map

- Display vector data
- Pan and zoom
- Select features
- Inspect attributes and geometry metadata

#### Capability panel

- Discover compatible operations
- Generate input forms from capability schemas
- Start jobs
- Display progress and diagnostics
- Preview, persist or discard results

#### Runtime status

- Display loaded plugins and versions
- Display health and active jobs
- Demonstrate side-by-side plugin replacement

### 13.3 Browser milestone exit criteria

- The workbench runs in a normal supported browser.
- It uses only the public TypeScript SDK.
- It can browse and display PostGIS data.
- It can invoke a replaceable buffer plugin.
- It shows progress, errors and provenance.
- It can persist a result.
- It remains usable while a plugin version is replaced.
- No Tauri code or API is required.

## 14. Tauri Desktop Shell, Milestone 2

Tauri begins only after the browser milestone exit criteria pass.

### 14.1 Desktop architecture

```text
Tauri 2
├── React production assets
├── native window and lifecycle
├── optional .NET Spatial.Host sidecar
└── narrow native adapters
```

### 14.2 Desktop modes

#### Bundled local mode

Tauri launches a platform-specific, self-contained .NET sidecar on a loopback endpoint. It waits for the readiness endpoint, loads the React UI and cleanly stops the child process during application shutdown.

#### Remote mode

Tauri loads the same React application configured to connect to a remote spatial host. No local spatial process is required.

### 14.3 Tauri tasks

- Package React production assets
- Package the .NET host for each target platform and architecture
- Implement sidecar startup and readiness checks
- Use an ephemeral or configured secure loopback endpoint
- Pass connection settings to the React client without embedding secrets
- Implement clean shutdown
- Add native file-picker adapter through a narrow capability
- Test Windows WebView2 first
- Add explicit compatibility tests before declaring macOS or Linux support
- Add desktop install, upgrade and rollback smoke tests

### 14.4 Desktop exit criteria

- The unchanged React workbench runs inside Tauri.
- The same TypeScript SDK is used.
- No spatial logic is added to the Rust shell.
- Bundled mode starts and stops the .NET host cleanly.
- Remote mode connects without bundling the host.
- Browser delivery remains independently supported.

## 15. Repository Structure

```text
/spatial-engine
  /src
    /Spatial.Core
    /Spatial.Runtime
    /Spatial.Host
    /Spatial.PluginSdk
    /Spatial.PluginHost.DotNet
    /Spatial.PluginHost.Wasm
    /Spatial.Provider.PostGIS
    /Spatial.Operations.NetTopologySuite
    /Spatial.Transformations.Proj

  /clients
    /typescript
    /dotnet
    /cli

  /apps
    /workbench-web
    /workbench-desktop-tauri

  /contracts
    /geometry
    /features
    /capabilities
    /schemas
    /wit

  /tests
    /unit
    /architecture
    /contracts
    /conformance
    /integration
    /performance
    /end-to-end-web
    /end-to-end-desktop

  /architecture
    /decisions
    principles.md
    core-boundary.md
    geometry-model.md
    capability-model.md
    plugin-lifecycle.md
    security-model.md
    interchange.md
    deployment-profiles.md
    frontend-boundary.md

  /eng
  AGENTS.md
  Directory.Build.props
  Directory.Packages.props
  README.md
```

## 16. Implementation Phases

### Phase 0: Architecture Guardrails

- Create repository and solution structure.
- Add root and package-level `AGENTS.md` files.
- Add architecture decision records.
- Add dependency rules and architecture tests.
- Add one-command build, format and test scripts.
- Establish .NET 10 and Node versions.
- Record that the host is initially JIT-compiled.
- Record that browser delivery precedes Tauri.

**Exit criteria**

- `Spatial.Core` references no database, renderer or spatial algorithm package.
- `Spatial.Runtime` references no concrete plugin.
- `Spatial.Host` runs without a desktop shell.
- Architecture tests catch forbidden dependencies.

### Phase 1: Core Geometry

**Status:** complete (Epic B).

- Implement coordinate layouts, CRS identity and envelope.
- Implement packed coordinate sequences.
- Implement immutable simple-feature geometry types.
- Implement traversal, builders and binary round trips.
- Add unit, property and allocation tests.

**Exit criteria**

- Every initial geometry type round-trips.
- NetTopologySuite is absent from core dependencies.
- Large sequences do not require one heap object per coordinate.

### Phase 2: Features and Schemas

**Status:** complete (Epic C).

- Implement typed attributes, fields, feature identity and schemas.
- Implement feature batches and versioned encoding.
- Add schema compatibility tests.

### Phase 3: Capability Runtime

**Status:** complete (Epic D).

- Implement capability descriptors, registry, resolution and invocation.
- Implement structured errors, deadlines, cancellation, progress and permissions.
- Add an in-memory component host and example capability.

### Phase 4: Resources, Streams and Jobs

**Status:** complete (Epic E).

- Implement opaque handles, ownership, leases and disposal.
- Implement bounded streaming and backpressure.
- Implement job state, events, cancellation and timeout behaviour.

### Phase 5: Native Plugin Packaging and Isolation

**Status:** complete (Epic F).

- Finalise the plugin manifest schema (ADR-0025, `architecture/plugin-manifest.md`).
- Implement separate-process .NET workers over language-neutral contracts
  (`Spatial.PluginHost.DotNet` worker host, `architecture/worker-protocol.md`).
- Add supervision, health checks, restart, side-by-side activation, draining
  and rollback (`architecture/plugin-lifecycle.md`).
- Add crash, timeout and cancellation fault fixtures.

### Phase 6: NetTopologySuite Operations Plugin

**Status:** complete (Epic G).

- Define buffer, intersection, validation and simplify contracts (`Spatial.PluginSdk.Operations`, ADR-0026, `architecture/operation-contracts.md`).
- Implement adapters without public NTS types (`Spatial.Operations.NetTopologySuite`, ADR-0005; `$geometry` canonical-binary wire tag, ADR-0020).
- Add shared conformance fixtures and provenance (`tests/conformance`, plan §18).

### Phase 7: Coordinate Transformation Plugin

**Status:** complete (Epic G).

- Define CRS description and transformation contracts.
- Implement the selected transformation library adapter.
- Add control-point, axis-order, error and tolerance tests.

### Phase 8: PostGIS Provider

**Status:** complete (Epic G).

- Implement catalogue, schema discovery, feature scan, filtering, streaming, writing and transactions.
- Integrate host-managed secrets and command cancellation.
- Add containerised integration tests.

Contract surface: `Spatial.PluginSdk.Providers` (ADR-0028,
`architecture/data-provider-contracts.md`) — `spatial.catalogue.list@1`,
`spatial.dataset.describe@1`, `spatial.dataset.create@1`,
`spatial.feature.scan@1`, `spatial.feature.query@1`,
`spatial.feature.write@1`, `spatial.transaction.begin/commit/rollback@1` —
implemented by `Spatial.Provider.PostGIS` (`postgis@1`) on Npgsql 10.
Feature data crosses as canonical binary (stream items and batch
arguments), metadata as JSON text items; secrets reach the worker through
its launch environment; containerised integration tests run on
Testcontainers PostGIS.

### Phase 9: ASP.NET Core Host and SDKs

- Implement HTTP, streaming, job, resource, plugin and health APIs.
- Generate and test TypeScript and .NET SDKs.
- Confirm the host runs independently through browser and automated clients.

### Phase 10: Browser Workbench

- Build the React and MapLibre workbench.
- Add catalogue, map, selection, capability forms, progress, result preview and persistence.
- Add runtime health and plugin-replacement views.
- Add Playwright browser tests.

This phase completes **Milestone 1**.

### Phase 11: Tauri 2 Desktop Packaging

- Create the thin Tauri shell.
- Package existing React assets.
- Add optional Spatial.Host sidecar packaging and supervision.
- Support remote-host mode.
- Add desktop-specific file adapter.
- Test Windows first, then explicitly validate other platforms.

This phase completes **Milestone 2**.

### Phase 12: WebAssembly Plugin Support

- Define WIT mappings.
- Implement permission-constrained WebAssembly component hosting.
- Add resource, execution and hostile-component tests.

### Phase 13: Native AOT Evaluation

Only after the runtime, plugins and deployment profiles are stable:

- Measure JIT host startup, memory and package size.
- Identify executables that do not require dynamic managed loading.
- Test trimming and AOT compatibility for those executables.
- Compare operational benefit against maintenance complexity.
- Keep the main host JIT-compiled if AOT compromises plugin or framework compatibility.

No AOT adoption occurs without measured benefit and complete compatibility tests.

## 17. First End-to-End Demonstration

1. Start the JIT-compiled .NET spatial host independently.
2. Load the PostGIS provider and NTS operations worker.
3. Open the React workbench in a normal browser.
4. Browse and display a PostGIS dataset.
5. Select features.
6. Discover and invoke `spatial.geometry.buffer@1`.
7. Stream job progress.
8. Preview and persist the result.
9. Start version 2 of the operations plugin.
10. Route new jobs to version 2.
11. Drain version 1 without stopping the host or browser UI.
12. Display provider and operation provenance.
13. After this demonstration passes, package the unchanged React application in Tauri.
14. Demonstrate both bundled-sidecar and remote-host desktop modes.

## 18. Testing Strategy

### Core and contract tests

- Geometry construction, immutability and encoding
- Coordinate layouts and CRS identity
- Schema compatibility
- Capability resolution
- Resource ownership
- Job transitions
- Plugin manifest compatibility

### Conformance tests

Every provider of a standard capability runs the same fixtures, including success, empty input, unsupported input, cancellation and diagnostics.

### Integration tests

Use realistic boundaries with containers and child processes for:

- Spatial.Host
- PostGIS
- Plugin workers
- Workbench API

### Web tests

Run the browser workbench independently using Playwright. Tauri must not be needed for these tests.

### Desktop tests

Run a smaller packaging and lifecycle suite that verifies:

- Sidecar launch and readiness
- React asset loading
- Clean child-process shutdown
- Remote-host mode
- File adapter permission boundaries

### Performance tests

Track:

- Geometry encoding throughput
- Allocation per geometry
- Feature-stream throughput
- Time to first feature
- Cross-process invocation overhead
- Worker startup time
- Plugin replacement interruption
- Browser map responsiveness
- Tauri sidecar startup separately from host startup

## 19. Security Model

- Plugins receive no ambient authority.
- Third-party plugins run out of process or in WebAssembly.
- Secrets remain host-managed and scoped.
- Tauri does not embed database credentials.
- A bundled host binds only to an appropriate local endpoint and validates the desktop client connection.
- Native adapters expose narrowly defined capabilities rather than arbitrary shell execution.
- Input and output contracts are validated.
- Payload, stream, memory and time limits are enforced where supported.
- Audit events and provenance are structured.

## 20. Agent-Driven Development Rules

Each agent task must define:

- One bounded outcome
- Permitted files or projects
- Prohibited architectural changes
- Contract references
- Acceptance tests
- Build and test commands
- Expected artefacts

Root agent instructions must include:

- Do not add spatial algorithms to `Spatial.Core`.
- Do not expose third-party types in public contracts.
- Do not reference concrete plugins from the runtime.
- Do not add Tauri dependencies to the web workbench or spatial host.
- Do not add spatial business logic to the Tauri shell.
- Keep the host independently executable.
- Do not enable Native AOT without an approved decision record and compatibility evidence.
- Add cancellation and diagnostics to long-running behaviour.
- Update tests and architecture records with public changes.

## 21. Initial Backlog

### Epic A: Repository and guardrails

- [x] Create .NET 10 solution and package structure
- [x] Add architecture tests
- [x] Add build scripts
- [x] Add agent instructions
- [x] Add initial decision records

### Epic B: Geometry foundation

- [x] Coordinate and layout types
- [x] CRS identity
- [x] Packed coordinate sequence
- [x] Geometry hierarchy
- [x] Envelope and traversal
- [x] Binary encoding
- [x] Builders and property tests

### Epic C: Features and batches

- [x] Attribute-value model
- [x] Schemas and feature identity
- [x] Feature batches
- [x] Batch encoding

### Epic D: Capability runtime

- [x] Descriptors and registry
- [x] Provider resolution
- [x] Invocation routing
- [x] Errors and diagnostics
- [x] Cancellation and permissions

### Epic E: Resources and jobs

- [x] Handles and leases
- [x] Streams and backpressure
- [x] Job state and events
- [x] Leak and cancellation tests

### Epic F: Plugin workers

- [x] Language-neutral worker protocol (ADR-0025, `architecture/worker-protocol.md`)
- [x] .NET worker SDK (manifest schema + worker host executable)
- [x] Process supervisor (discovery, validation, activation, health, restart)
- [x] Health and restart
- [x] Side-by-side activation (active-preference routing)
- [x] Draining and rollback

### Epic G: Spatial implementations

- [x] NTS adapters and operations
- [x] Transformation provider
- [x] Shared conformance suite
- [x] PostGIS provider

### Epic H: Host and browser workbench

- [ ] ASP.NET Core API
- [ ] TypeScript SDK
- [ ] React shell
- [ ] Dataset browser
- [ ] Map display
- [ ] Capability forms
- [ ] Result preview and save
- [ ] Browser plugin-replacement demonstration

### Epic I: Tauri desktop

- [ ] Thin Tauri shell
- [ ] React asset packaging
- [ ] Platform-specific .NET sidecar packaging
- [ ] Sidecar readiness and shutdown
- [ ] Remote-host configuration
- [ ] Narrow file adapter
- [ ] Windows desktop tests
- [ ] Explicit macOS and Linux compatibility evaluation

## 22. Definition of Done

A work item is complete only when:

- Required behaviour is implemented.
- Public contracts are versioned.
- Success, failure and cancellation are tested where applicable.
- No prohibited dependency is introduced.
- Diagnostics are actionable.
- Documentation is updated.
- Build, formatting and tests pass from a clean checkout.
- New public behaviour has an example or fixture.
- Performance-sensitive changes include a baseline.
- Browser functionality does not depend on Tauri.
- Desktop functionality does not duplicate spatial business logic.

## 23. Major Risks and Mitigations

### Core expansion

**Risk:** Convenience methods turn core into a monolithic spatial library.  
**Mitigation:** Enforce the core inclusion test and architecture tests.

### Third-party type leakage

**Risk:** NTS, Npgsql or renderer types become public contracts.  
**Mitigation:** Keep adapters private and inspect public API surfaces.

### Fragile plugin reload

**Risk:** In-process unloading is blocked by statics, threads or native dependencies.  
**Mitigation:** Use isolated process replacement by default.

### Cross-process overhead

**Risk:** Isolation makes large geometry processing slow.  
**Mitigation:** Binary batches, streams, handles and provider pushdown.

### Desktop-first coupling

**Risk:** Tauri APIs leak into the workbench or runtime.  
**Mitigation:** Complete the independent browser milestone before creating the Tauri shell.

### Webview variation

**Risk:** UI or WebGL behaviour differs between operating-system webviews.  
**Mitigation:** Target and test Windows first, then explicitly validate other platforms before claiming support.

### Premature Native AOT

**Risk:** AOT compromises dynamic loading or framework compatibility before it provides useful benefit.  
**Mitigation:** Remain JIT-compiled initially and require measurement plus compatibility tests before adoption.

### Technology sprawl

**Risk:** .NET, TypeScript, Rust, WebAssembly and spatial libraries create excessive complexity.  
**Mitigation:** Keep Rust confined to the thin Tauri shell until a measured use case justifies more.

### Agent architecture drift

**Risk:** Agents solve local problems by bypassing boundaries.  
**Mitigation:** Bounded tasks, package instructions, dependency tests and review gates.

## 24. Architecture Decisions to Record

```text
ADR-0001 Geometry is part of the spatial core
ADR-0002 Spatial operations are capability plugins
ADR-0003 Data stores are capability plugins
ADR-0004 Core geometry is immutable
ADR-0005 Third-party geometry types do not cross contracts
ADR-0006 Isolated worker processes are the default plugin boundary
ADR-0007 Capability contracts are versioned independently
ADR-0008 Long operations use the job model
ADR-0009 CRS identity is core; transformation is a plugin
ADR-0010 Initial backing provider is PostGIS
ADR-0011 .NET 10 is the initial backend and runtime
ADR-0012 The initial .NET host is JIT-compiled
ADR-0013 Worker boundaries are language-neutral
ADR-0014 React, TypeScript and MapLibre form the first frontend
ADR-0015 Browser delivery is completed before desktop packaging
ADR-0016 Tauri 2 is the first desktop shell
ADR-0017 Tauri contains no spatial business logic
ADR-0018 Spatial.Host remains independently executable
ADR-0019 Tauri may bundle Spatial.Host as an optional sidecar
ADR-0020 Canonical binary interchange is required
ADR-0021 Native AOT requires measured benefit and compatibility evidence
ADR-0022 Resource handles are runtime-owned with leases
ADR-0023 Bounded streams carry the backpressure
ADR-0024 Jobs are observable state machines with events and timeouts
ADR-0025 Worker boundaries speak versioned line-delimited JSON with runtime-owned facilities
ADR-0026 Standard geometry operations are versioned capability contracts
ADR-0027 Coordinate transformation contracts and the ProjNet adapter
ADR-0028 PostGIS provider contracts and data interchange
```

## 25. Recommended Starting Sequence

1. Repository guardrails and architecture tests
2. Core coordinate sequence and Point
3. LineString and Polygon
4. Multi-geometries and binary round trip
5. Feature and schema model (Phase 2, complete)
6. Capability descriptor, registry and invocation
7. Out-of-process .NET example worker
8. Resource handles, streams and jobs
9. NetTopologySuite buffer plugin
10. PostGIS feature scan and write
11. Independently executable ASP.NET Core host
12. Generated TypeScript SDK
13. Browser-hosted React and MapLibre workbench
14. Browser side-by-side plugin replacement demonstration
15. Thin Tauri 2 shell using unchanged React assets
16. Bundled .NET sidecar and remote-host desktop modes
17. WebAssembly support
18. Native AOT evaluation only if justified by measurements

The first architectural milestone is complete when the same core geometry travels from PostGIS to a replaceable operation worker, returns through the .NET runtime, renders in a normal browser and can be written back without the client depending on PostGIS or NetTopologySuite types.

The desktop milestone is complete when that same browser application runs unchanged in Tauri, with the independently executable .NET host either bundled as a cleanly supervised sidecar or accessed remotely.
