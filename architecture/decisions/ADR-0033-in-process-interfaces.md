---
status: accepted
date: 2026-09-10
deciders: maintainer + agent
---

# ADR-0033: Replace worker plugins with in-process interfaces and DI

## Context

The worker plugin model (ADR-0002/0006/0013/0025: versioned capability
contracts, `spatial.worker/1` line-delimited JSON, `ValueCodec` facility
RPCs, `PluginHost.DotNet` supervision, `PluginPacker` manifests,
side-by-side `nts@1/nts@2` + `drain/rollback`) costs more than it returns
for a single-team, trusted, .NET-only deployment where restart-to-upgrade
is acceptable: dual conformance matrix, packaging/e2e harnesses, codec
mirrors (`sgeom.ts`), and supervisor lifecycle code dominate change cost.

## Decision

Capabilities become plain C# interfaces in `Spatial.PluginSdk` (slimmed to
abstractions), resolved by Microsoft DI. No worker processes, no manifest,
no language-neutral wire, no jobs/resources/streams infrastructure:

- `Spatial.Core` stays values-only (zero refs; `Core_has_no_dependencies` stands).
- `Spatial.PluginSdk` holds `IGeometryOperations`, `ICoordinateTransforms`,
  `ICrsDirectory`, `IDataCatalogue`, `IFeatureStore`, `ITransactionStore`,
  `IDemoJobs`, `SpatialException`, plus shared DTOs (`DatasetSummary`,
  `DatasetDescription`, `CrsDescription`, `BoundingBox`). No
  `CapabilityId/ProviderId/Invocation/Result`, no `ValueCodec`, no
  resources/streams/jobs, no `Http` invocation envelope.
- Implementations live in the existing projects and are linked by
  `Spatial.Host` (`NTS`, `ProjNet`, `Demo`, `PostGIS`). Third-party types
  stay inside implementations (ADR-0005 stands).
- Long work is plain `Task` + `CancellationToken` (+ `IProgress<double>`
  for sleep); feature reads return `IReadOnlyList<FeatureBatch>` instead of
  bounded streams; transactions are string handles owned by the store.
- The HTTP API is typed routes per verb (no generic `/api/invocations`,
  no `/api/jobs`, `/api/resources`, `/api/plugins` control endpoints).
- Secrets come from `IOptions` (`PostgisOptions.ConnectionString`), never
  from a worker launch environment.

## Consequences

- Deleted: `Spatial.Runtime`, `Spatial.PluginHost.DotNet`,
  `eng/tools/PluginPacker`, worker protocol, `route-new-work/drain/rollback`,
  `nts@2` side-by-side, worker conformance leg, `Spatial:PackagesRoot/
  Preferences/WorkerEnvironment` config.
- Lost (accepted): crash isolation, no-restart upgrade, multi-language
  workers, ambient-authority sandboxing. Third-party code is fully trusted.
- Kept: immutable Core values + `GeometryCodec`/`FeatureBatchCodec`
  (Base64 in typed DTOs), deterministic DI composition, actionable
  `invalid.arguments` errors, containerised PostGIS integration tests.
