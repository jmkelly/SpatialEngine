---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0064: Feature attachments are an additive SDK capability with provider-owned bytes

## Context

ADR-0061 §5 closed the T-038 write-model track with the attachment surface
honest but unserved: `queryAttachments` and the per-feature `attachments`
resource report the truthful empty set, the writes fail as typed
`invalid.arguments`, and no layer advertises `hasAttachments` — because no
dataset carries attachment blobs. That record deferred the store-model
decision to implementation sub-tasks of T-038 (this track, T-060): a new
additive SDK capability in the ADR-0037 pattern, a persistence decision per
provider, and a quota/auth story for the single-admin-token model. Serving
the HTTP surface on top of the capability is T-061 and is explicitly not
this record: the facade stays empty-reads plus typed write rejects,
unadvertised, until T-061 lands.

The constraints are the AGENTS.md hard walls: `Spatial.PluginSdk`
references only `Spatial.Core` and takes no packages; public contracts carry
only core types; long-running work is a cancellable `Task`; failures are
structured `SpatialException` codes.

## Decision

**Attachments are an additive `IFeatureAttachmentStore` capability alongside
`IFeatureEditStore`: per-feature blob put/get/delete keyed by layer dataset
and object id, provider-owned bytes, core-typed descriptors on the contract.
The memory provider implements it now; PostGIS persists it in a sidecar
table with `bytea` content (implementation deferred); writes are
admin-token-gated at the adapter while the store enforces its own byte cap.**

### 1. The contract (`Spatial.PluginSdk`, no new packages)

`IFeatureAttachmentStore` (in `Spatial.PluginSdk.Providers`, next to
`IFeatureEditStore`) carries only core and BCL types:

- `FeatureAttachmentDescriptor(long Id, string Name, string ContentType,
  long Size, string? Keywords)` — the provider-neutral face of an S4
  attachment-info. Esri shaping stays in the adapter.
- `FeatureAttachmentContent(Descriptor, byte[] Content)` — one blob with
  its bytes. The store copies on write and on read, so callers only ever
  hold provider-owned copies and can never mutate stored bytes through a
  previously returned array.
- `FeatureAttachmentOutcome(long Id, bool Succeeded, string? ErrorCode,
  string? ErrorMessage)` — the `FeatureEditOutcome` mirror for deletes:
  one outcome per id in input order.
- `ListAsync` (id-ordered descriptors, empty means none stored),
  `AddAsync` (assigns the next per-feature id starting at one),
  `GetAsync`, `UpdateAsync` (full replace, keeps the identity),
  `DeleteAsync` (per-id outcomes).

Failure codes follow the standing walls: an unknown dataset or feature is
`not.found`; an empty name, null content or over-quota bytes are
`invalid.arguments`; a missing attachment id on get/update is `not.found`,
on delete a per-id outcome failure. Every method observes its
`CancellationToken` before touching state.

`IStoreRegistry` gains the additive `AttachmentStore(string)` face, which
returns `null` when the store holds no blobs — the same honesty pattern as
`EditStore`. Stores without blob support (demo, ArcGIS REST, PostGIS until
its track lands) simply omit the capability.

### 2. Persistence per provider

- **Memory:** implemented here (`MemoryAttachments`, split from
  `MemoryStore` like `MemoryEditor`). Non-durable like the rest of the
  provider; keyed by dataset and `FeatureId` with per-feature integer ids
  from one. No transaction handles: attachment operations are not enlisted
  in edit transactions, matching the Esri operations which carry no
  transactional semantics.
- **PostGIS:** a sidecar table (one row per attachment: dataset, feature
  identity, attachment id, name, content type, size, keywords, `bytea`
  content) — transactional with the surrounding work, discovered-identifier
  statements in the `PostgisQueries` style, no new infrastructure.
  Implementation is a follow-up task, not this track: until it lands the
  PostGIS key exposes no attachment capability and the facade keeps its
  honest empty reads and typed rejects for PostGIS-backed layers.

### 3. Quota and auth (single-admin-token model)

- **Quota:** each provider enforces its own per-attachment byte cap and
  rejects over-quota puts as `invalid.arguments`. The memory default is 10
  MiB. Per-feature or per-dataset totals are deferred to the serving track
  (T-061), which will observe real client sizes before capping aggregates.
- **Auth:** attachment reads follow feature-query auth (public, like the
  features they annotate); attachment writes (add/update/delete) require
  the single admin token, enforced at the adapter exactly like the
  `EsriAdminEndpoints` gate. The SDK stays auth-free (in-process callers
  are already inside the trust boundary). Enforcement lands with the T-061
  routes, not here.

## Consequences

- New contract surface only: `IFeatureAttachmentStore.cs` in the SDK, the
  `AttachmentStore` registry face, `MemoryAttachments` in the memory
  provider, host DI wiring for the `memory` key. The adapter, the replay
  fixtures and the WFS ids are untouched; nothing is advertised and no
  endpoint changes behaviour.
- Behaviour lands with contract, SDK, test (unit red-first plus host
  wiring) and this ADR together, with success, failure and cancellation
  covered.
- The PostGIS sidecar-table implementation and the T-061 serving surface
  (hasAttachments/queryAttachments/advertising plus the admin-token write
  gate) are separate, dep-blocked tracks.

## Alternatives

- **PostGIS large objects:** server-side streaming for very large files,
  but large objects live outside transaction semantics and need
  `vacuumlo` maintenance — operational burden for blobs that fit TOASTed
  `bytea` rows. Rejected; if client sizes ever outgrow `bytea`, that is a
  new task with measurements.
- **Sidecar object store (S3-style):** would serve multi-host deployments,
  but invents storage infrastructure with no requesting client trace and
  splits blobs from the transactional dataset. Rejected for the same
  reason the jobs model stays deleted.
- **Bytes as feature attributes:** would reuse the edit path, but breaks
  the schema/edit contract (binary columns in `FeatureSchema`, identity
  and validation semantics) and leaks provider encoding into queries.
  Rejected; blobs are keyed alongside features, never inside them.
