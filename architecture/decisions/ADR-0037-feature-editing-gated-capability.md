---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0037: Feature editing is a gated, per-feature store capability

## Context

ADR-0035 §5 committed the GeoServices adapter to read-only first and gated
Feature Service editing (`addFeatures` / `updateFeatures` / `deleteFeatures`
/ `applyEdits`, spec §9.1.6–§9.1.9) on a follow-up ADR "extending
`IFeatureStore` with update and delete (and corresponding PostGIS SQL) plus
per-feature edit results". The engine's `IFeatureStore` is append-only and
the demo / ArcGIS REST stores are read-only by design, so editing cannot be
a method on every store without forcing read-only implementations to carry
dead verbs.

Two further facts shape the decision:

- **Hosted layers need a durable, client-round-trippable key.** The
  read-only facade exposes a synthetic integer `OBJECTID` derived from scan
  order; that is fine for `query`, but an edit must target a feature after
  later writes, so the key must be the dataset's own identity.
- **`applyEdits` is atomic per the spec's "stateless because REST" model**
  and the facade must be able to report which feature failed.

## Decision

**1. Editing is a separate, additive SDK interface.**
`Spatial.PluginSdk.Providers.IFeatureEditStore` (alongside
`IFeatureStore`, not extending it) exposes `AddAsync`, `UpdateAsync` and
`DeleteAsync`, each returning one `FeatureEditOutcome` per input in input
order. This follows ADR-0036's granular-face rule: a store advertises the
capability it owns, and a read-only store simply does not implement it.
The facade advertises `Query` alone for such layers and rejects edit routes
with `invalid.arguments`.

**2. Editing requires an integer identity.**
`EsriObjectIdScheme` maps `OBJECTID` to the dataset's single integer
identity column when one exists; the synthetic scan-ordinal `OBJECTID` of
the read-only facade remains supported for `query` but cannot be edited.
This keeps a durable key without inventing one, and matches Esri's
`objectIdField` semantics.

**3. The adapter owns protocol semantics, not algorithms; stores own SQL.**
`Spatial.Adapter.GeoServices` decodes Esri features, merges partial updates
by reading the existing feature, maps outcomes to
`{objectId, globalId, success, error}`, and lets `rollbackOnFailure` use the
store's `ITransactionStore`. `PostgisEditStore` (composed with
`PostgisStore`, so the store keeps one cohesive read/write/transaction
responsibility) implements `IFeatureEditStore` with `UPDATE`/`DELETE` and
`INSERT … RETURNING` built only from discovered identifiers and bound
parameters. The demo and ArcGIS REST stores stay read-only.

**4. The per-feature result shape is shared wire code.**
`Spatial.Interop.Esri.EsriEditResult` and `EsriEditResultCodec` write the
result arrays; `Spatial.Interop.Esri` still references `Spatial.Core` only.

**5. The v1.0 baseline is enforced by rejection.**
`gdbVersion`, `useGlobalIds` and `returnEditResults` are rejected
explicitly; `rollbackOnFailure` is accepted. Service-level `applyEdits`,
attachments and `queryRelatedRecords` stay out of scope (no relationship or
attachment model).

## Consequences

- `Spatial.Core` and the existing `IFeatureStore` are unchanged; adding the
  capability touches the SDK, the adapter, the interop codec, `PostgisStore`
  and the host registration.
- Update is read-modify-write, so a partial update merges unchanged fields
  from the existing feature. ADR-0038 now delivers the store-level
  read-by-id optimisation (`IFeatureLookup`), so the facade resolves edits
  through one identity-targeted read instead of scanning the whole dataset;
  stores without the capability keep the scan fallback.
- An update matches its row by the feature's pre-edit identity
  (`Feature.Id`, bound after the SET parameters), never by the new
  identity-attribute values, so re-keying an identity column reports a
  per-feature constraint failure instead of retargeting the update onto a
  different row (fixed under T-002).
- Adds to a serial-identity table require the client to supply the identity
  (the engine `Feature` model has no "unassigned" state). A future
  `IFeatureEditStore` revision may add an explicit "omit identity on insert"
  mode.
- The `where` subset, WKID map and payload limits of ADR-0035 apply
  unchanged to edits; no client text becomes SQL structure.
- Capability and field `editable` flags are derived from the store and the
  identity scheme, so a read-only deployment cannot accidentally advertise
  editing.

## References

- ADR-0035 (GeoServices boundary adapter), ADR-0036 (granular geometry verbs),
  ADR-0038 (read-by-identity store capability)
- `architecture/geoservices-implementation-plan.md` §5 (S3)
- `architecture/references/geoservices-compatibility.md`
- ArcGIS REST API (online, checked 2026-09-11):
  [addFeatures](https://developers.arcgis.com/rest/services-reference/enterprise/add-features/),
  [updateFeatures](https://developers.arcgis.com/rest/services-reference/enterprise/update-features/),
  [deleteFeatures](https://developers.arcgis.com/rest/services-reference/enterprise/delete-features/),
  [applyEdits](https://developers.arcgis.com/rest/services-reference/enterprise/apply-edits/),
  [Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/)
