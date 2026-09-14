---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0066: Feature attachments are served on the attachment-store capability

## Context

ADR-0061 §5 closed the T-038 write-model track with the attachment surface
honest but unserved: `queryAttachments` and the per-feature `attachments`
resource report the truthful empty set, the writes fail as typed
`invalid.arguments`, and no layer advertises `hasAttachments` — because no
dataset carried attachment blobs. ADR-0065 then landed the store model as
T-060: the additive `IFeatureAttachmentStore` capability alongside
`IFeatureEditStore` (per-feature blob put/get/delete keyed by layer dataset
and object id, provider-owned bytes, core-typed descriptors), implemented by
the memory provider with a 10 MiB per-attachment cap, while PostGIS (sidecar
table) stays a follow-up track. ADR-0065 explicitly defers serving the HTTP
surface to T-061: "the facade stays empty-reads plus typed write rejects,
unadvertised, until T-061 lands". This record is that serving decision.

The constraints are the AGENTS.md hard walls: `Spatial.PluginSdk`
references only `Spatial.Core` and takes no packages; public contracts carry
only core types; long-running work is a cancellable `Task`; failures are
structured `SpatialException` codes. No SDK change ships here — the T-060
capability is served as-is.

## Decision

**The adapter serves the attachment surface on the `IFeatureAttachmentStore`
capability: layers backed by a capable store advertise `hasAttachments` with
`attachmentProperties`, reads list and serve the stored blobs, and the three
writes mutate them behind the single admin token. Stores without the
capability keep the ADR-0061 honesty (empty reads, typed write rejects,
`hasAttachments: false`).**

### 1. Advertising

A layer whose store exposes `AttachmentStore` reports `hasAttachments: true`
with `attachmentProperties` naming exactly the descriptor faces the
capability serves — `id`, `name`, `size`, `contentType`, `keywords`, all
enabled. Every other layer reports `hasAttachments: false` and omits
`attachmentProperties`. The flag is per-store, not per-feature: a capable
but blob-less layer still advertises, because a client can upload the first
attachment.

### 2. Reads (public, like the features they annotate)

- `.../{layerId}/queryAttachments` returns the S4 shape
  `{"attachmentGroups": [{"parentObjectId", "attachmentInfos": [...]}]}` —
  one group per requested `objectIds` value in request order, or per feature
  in scan order when `objectIds` is absent. Malformed `objectIds` is
  `invalid.arguments`; an unknown object id is `not.found`, never silently
  dropped.
- `.../{layerId}/{objectId}/attachments` returns
  `{"attachmentInfos": [...]}` for one feature in id order.
- `.../{layerId}/{objectId}/attachments/{attachmentId}` serves the stored
  bytes with their content type. Unknown features and unknown attachment ids
  are `not.found`.
- Object ids resolve exactly as `query` and the feature resource do (the
  identity column when the layer has one, otherwise the scan ordinal), so
  attachments agree with `returnIdsOnly`.
- Without a capability the reads stay truthful: per-feature empty infos
  (nothing is stored for any feature).

### 3. Writes (single-admin-token-gated, ADR-0065 §3)

`addAttachment` / `updateAttachment` take a multipart form upload (the file
part named `attachment`, an optional `keywords` field) and return the S4
result shapes (`addAttachmentResult` / `updateAttachmentResult` carrying the
assigned or kept attachment id); `deleteAttachments` takes `attachmentIds`
and returns one `deleteAttachmentResults` entry per id in input order, with
per-id typed errors on partial failure (the `applyEdits` pattern). A missing
file part, a missing `attachmentId`/`attachmentIds`, an unknown feature, an
unknown attachment id on update, and over-quota bytes fail as the store's
structured codes mapped onto the Esri envelope; without a capability the
writes fail as typed `invalid.arguments` naming the missing capability.

Attachment writes require the single admin token, enforced at the adapter
exactly like the `EsriAdminEndpoints` gate (constant-time comparison,
Bearer header or `token` parameter; unconfigured means unavailable,
missing means token-required, wrong means invalid). Attachment reads stay
public. The SDK stays auth-free: in-process callers are already inside the
trust boundary. Per-feature and per-dataset aggregate quotas stay deferred:
T-061 observes real client sizes before capping aggregates, as ADR-0065 §3
decided; only the store's per-attachment byte cap applies.

### 4. Failure and cancellation

Every operation observes its `CancellationToken` before touching state and
passes it to the store; unknown layers and features are `not.found`;
malformed ids are `invalid.arguments`. Behaviour lands with contract
(unchanged — adapter-only), test (adapter unit plus HTTP red-first) and doc
(`geoservices-compatibility.md` §3) updates together, with success, failure
and cancellation covered.

## Consequences

- New adapter surface only: the `FeatureAttachments` operation engine
  (`QueryAsync`/`InfosAsync`/`ContentAsync`/`AddAsync`/`UpdateAsync`/
  `DeleteAsync` plus the S4 response shapes), the served routes in
  `GeoServicesEndpoints.FeatureOps` (including the content resource), the
  admin-token gate threaded from host configuration, and the
  `hasAttachments`/`attachmentProperties` layer metadata. No SDK, provider
  or Core change.
- The PostGIS sidecar-table persistence remains the follow-up track:
  PostGIS-backed layers keep the honest surface until it lands.
- `supportsAttachmentsByUploadId`, `attachmentFields`, `globalIds` and
  `attachmentsDefinition` stay unserved: no upload-id workflow, no physical
  attachment table, no global-id identity for attachments.

## Alternatives

- **Gating writes on the edit capability instead of the admin token:**
  rejected — editing (`applyEdits`) is ungated on capable stores, while
  ADR-0065 §3 decided attachment bytes need the admin gate until a richer
  auth model exists. The two gates stay independent.
- **Advertising per-feature (only features with blobs report):**
  rejected — `hasAttachments` is a layer flag in the spec, and a capable
  layer must advertise before the first upload or clients can never
  discover upload support.
- **Skipping the content resource (metadata only):** rejected — without the
  bytes, attachments are write-only metadata no client can render; the
  resource is the read half of the store's `GetAsync`.
