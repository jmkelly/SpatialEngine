---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: `Feature.Id` is the identity column's value for every store that declares one, so a read by identity asks the key's own question.
---

# ADR-0119: A store's `Feature.Id` is the identity column's value

## Context

`IFeatureLookup` (ADR-0038) is keyed by `Feature.Id`, and
`EsriObjectIdScheme.ToFeatureId` turns an Esri `OBJECTID` into a store
`FeatureId` by rendering it as its decimal string. That is only a correct
inverse of the store's key if the store keys its features by the layer's
identity column — which ADR-0038 asserted about the stores and
ADR-0112 §Context point 2 found to be false of one of them.

The in-memory provider (ADR-0042) was the exception. `IngestSchema` numbers
features as it reads them, so a GeoJSON ingested with
`identity=source&identityField=id` whose `id` runs 10..13 arrives as
`FeatureBatch` pages with `Feature.Id` 1..4; `MemoryIngestPlan.Materialise`
stored each row as it was handed over, so the dataset's identity column said
10..13 while `Feature.Id` said 1..4. Two faces then answered about different
features:

- `FeatureAttachmentTargets.FindFeatureAsync` asked the store for
  `FeatureId("13")`, got nothing, and fell back to the scan ADR-0112
  §Decision 5 installed. The answer was right; the keyed read bought nothing
  on the one store that a hosted ingest always reaches.
- `FeatureEditEngine.ResolveAsync` asked the same question and treated a miss
  as `not.found`. On a source-identity layer a `updateFeatures` for `OBJECTID`
  12 was refused for a feature that exists, and `relate` wrote nothing.

PostGIS and SQL Server were never affected: their row mappers build
`Feature.Id` from the row's primary-key columns, so the identity column's
value *is* the key there, and the invariant held by accident of the reader
rather than by anything the ingest path promised.

## Decision

**`Feature.Id` is the identity column's value, for every store that declares
an identity column.** A dataset's identity column is its key, and
`Feature.Id` is the engine's name for that key; a store that read rows back
under any other name would make a read-by-identity answer a question about a
different feature.

This is a statement about the *stored* feature, so the ingest path is where
it is enforced, not the decode. `MemoryIngestPlan.Materialise` re-keys every
stored feature on its identity column through `MemorySchema.Rekey`: an
`IngestIdentity.Auto` row keeps the assigned id it already carries, an
`IngestIdentity.Source` row takes the value of the column the request named,
and a dataset with no identity column keeps the caller's identity untouched.

The codec is deliberately not where this lives. `IngestSchema` numbering is a
decode-local artefact — the order the document was read in — and the mode that
decides whether an identity is even wanted (`None`, `Auto`, `Source`, and the
`IdentityField` that goes with it) is the *store's* answer to `IngestRequest`,
resolved by each store's ingest plan. Setting `Feature.Id` in the codec would
have to be undone by every store that assigns its own identity, and the two
databases would still be the only places the invariant is true.

**ADR-0112 §Decision 5's scan-on-miss stays.** Keying the targeted read's
result by the `OBJECTID` the row carries, and falling back to the scan when
the targeted read resolves nothing, is still what makes a mis-keyed store
harmless. The rule above removes the reason the engine's own stores needed the
fallback; it does not remove the reason a third-party `IFeatureLookup`
implementation might.

## Consequences

- **A source-identity layer behaves like every other identity-backed layer.**
  `GeoServicesSourceIdentityTests` ingests with
  `identity=source&identityField=id` over HTTP and asserts the per-feature
  resource, the attachment resource and `updateFeatures` all resolve with
  `Lookups > 0` and `Scans == 0`, and that the same layer on a store without
  the lookup face answers identically with one scan. Three of its six tests
  fail on the pre-change store: the two resource reads scanned, and the
  update was a `not.found` for a feature that existed.
- **The hosted relationship tests keep their answers.** `relate`/`unrelate`
  resolve their origin records through the lookup and the traversal still
  reads the related layer once, which is the traversal's own read rather than
  a target resolution.
- **The decode still numbers features as it reads them.** A caller that wants
  the identity in `Feature.Id` before a store has taken the pages has no way
  to ask for it; that is the codec's own contract and is unchanged here.
- **An `IngestIdentity.Source` ingest whose identity column is null fails.**
  A source identity that is not a value cannot key a feature, and the ingest
  rejects it rather than storing a row whose key is a different feature's.
- **A plain `IFeatureStore.WriteAsync` into an identity dataset is not
  re-keyed.** The caller states the identity there in `Feature.Id`; a batch
  whose identity column disagrees with its `Feature.Id` is a contradiction
  this record does not adjudicate, and the same question applies to the
  PostGIS and SQL Server write paths. It is a follow-up, not a claim.

## Alternatives

- **Give `IFeatureLookup` a shape naming the column it keys on**, so the
  adapter could ask a store by identity-column value rather than by
  `Feature.Id`. The most general answer, and a contract change to every store
  for a case the engine's own stores could not exhibit once this record is in
  force. Rejected as the wrong layer to fix a mis-keyed row in.
- **Have the adapter resolve the identity column's value off the fetched row
  and ignore the id it asked with** — what ADR-0112 §Decision 5 already does,
  and why the pre-change code still scanned. Correct, but it pays the scan.
- **Set `Feature.Id` from the identity field inside `IngestSchema`.** Smaller
  and arguably where the number is invented, but it needs the identity *mode*
  in the decode, it would make the codec answer a store's question, and the
  database stores would have to discard the value on the way in. Rejected.
- **Drop the scan-on-miss while the invariant is in force.** Cheaper, and it
  turns a store that does not honour the invariant into a served 404 —
  the failure ADR-0112 §Alternatives already refused once.

## References

- ADR-0038 (read-by-identity, `IFeatureLookup`), ADR-0037 (the synthetic
  `OBJECTID` and the edit store), ADR-0042 (the in-memory provider),
  ADR-0041 (the ingest face and identity modes), ADR-0112 §Context 2 and
  §Decision 5 (the scan-on-miss this record makes unnecessary on the engine's
  own stores), ADR-0043 (an unassigned identity the store fills in).
- `src/Spatial.Stores.Memory/MemoryIngestPlan.cs` (`Materialise`),
  `src/Spatial.Stores.Memory/MemorySchema.cs` (`Rekey`),
  `src/Spatial.Adapter.GeoServices/EsriObjectIdScheme.cs` (`ToFeatureId`),
  `src/Spatial.Adapter.GeoServices/FeatureAttachmentTargets.cs`,
  `src/Spatial.Adapter.GeoServices/FeatureEditEngine.cs` (`ResolveAsync`),
  `tests/unit/Spatial.Stores.Memory.Tests/MemorySourceIdentityTests.cs`,
  `tests/integration/Spatial.Host.Tests/GeoServicesSourceIdentityTests.cs`.
